// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>
    /// A JPEG 2000 Part 1 encoder (ITU-T T.800): component planes in, a codestream out. Lossless
    /// through the DC shift, the RCT, the 5-3 reversible wavelet and no quantisation
    /// (<see cref="EncodeLossless(long[][], int, int, int, bool, JpxEncodeOptions)"/>); lossy through the ICT, the 9-7 irreversible wavelet and
    /// scalar expounded quantisation at a caller-chosen step (<see cref="EncodeLossy(long[][], int, int, int, bool, JpxEncodeOptions)"/>). As many
    /// quality layers as asked, each taking the coding passes that remove the most distortion per
    /// byte until its share of the target is spent (<see cref="JpxRateControl"/>), the last taking
    /// everything left unless a size target truncates it; no code-block style flag.
    /// <see cref="WrapAsJp2"/> puts a codestream in the JP2 file format of Annex I.
    /// </summary>
    /// <remarks>
    /// <para><b>The geometry is the decoder's.</b> The SIZ and COD the encoder writes are read back
    /// through <see cref="JpxImageSize"/> and laid out by <see cref="JpxTileLayout"/>, so tiles,
    /// resolution levels, sub-bands, precincts, code-blocks and the packet order
    /// (<see cref="JpxPacketSequence"/>) come from the one implementation of Annex B the decoder
    /// uses; the forward wavelet places its sub-bands by <see cref="JpxGeometry.Subband"/> likewise.
    /// Tier 1 shares the decoder's context model (<see cref="JpxBlockContexts"/>).</para>
    ///
    /// <para><b>Guard bits are chosen from the data.</b> Each band's exponent is its nominal range
    /// R<sub>b</sub> = R<sub>I</sub> + log<sub>2</sub> gain<sub>b</sub> (E-4, Table E.1), and the
    /// number of guard bits G is the smallest, at least 2, that makes M<sub>b</sub> = G + ε<sub>b</sub> − 1
    /// (E-2) hold every code-block's magnitude bit-planes — so no coefficient is ever truncated,
    /// and a pathological input that would need more than the seven a QCD can signal is refused
    /// rather than mis-coded.</para>
    /// </remarks>
    internal static class JpxEncoder
    {
        // Marker codes: T.800 Table A.2. Private to the codec, as the parser's are.
        private const int SOC = 0xFF4F, SIZ = 0xFF51, COD = 0xFF52, QCD = 0xFF5C, COM = 0xFF64, CAP = 0xFF50;

        /// <summary>SIZ's Rsiz bit 14: the codestream is HTJ2K (ITU-T T.814 §A.2).</summary>
        internal const int RsizHighThroughput = 0x4000;

        /// <summary>CAP's Pcap bit for Part 15, the 15th most significant of 32 (T.814 §A.3.1).</summary>
        internal const long Pcap15 = 1L << (32 - 15);
        private const int SOT = 0xFF90, SOD = 0xFF93, SOP = 0xFF91, EOC = 0xFFD9;

        /// <summary>The deepest samples this encoder takes: T.800's 38 bits (Table A.11), in <c>long</c> planes; the byte facade stops at 16.</summary>
        internal const int MaxPrecision = 38;

        /// <summary>The guard bits every codestream gets unless its data needs more (E.2: "typical values are G = 1 or G = 2").</summary>
        internal const int DefaultGuardBits = 2;

        /// <summary>The most guard bits a QCD's three bits signal (Table A.28).</summary>
        internal const int MaxGuardBits = 7;

        /// <summary>
        /// The most magnitude bit-planes any codestream signals for a band: M<sub>b</sub> = G + ε<sub>b</sub> − 1
        /// (E-2) with G at most <see cref="MaxGuardBits"/> and ε<sub>b</sub> at most
        /// <see cref="JpxForwardQuantizer.MaxExponent"/>. It is why 38-bit samples code
        /// irreversibly (the step's exponent clamps, so the quantised magnitudes fit) but
        /// losslessly only while the 5-3 coefficients need no more planes than this.
        /// </summary>
        internal const int MaxCodedBitPlanes = MaxGuardBits + JpxForwardQuantizer.MaxExponent - 1;

        /// <summary>A tile-part header as written here: the SOT marker segment (12 bytes) and SOD (2).</summary>
        internal const int TilePartHeaderBytes = 14;

        /// <summary>
        /// What <see cref="WrapAsJp2"/> adds around a codestream whose components share one depth
        /// and whose colour a JP2 names (greyscale, sRGB): the signature box (12), ftyp (20), jp2h
        /// (8) holding ihdr (22) and colr (15), and the jp2c box header (8).
        /// </summary>
        internal const int Jp2ContainerBytes = 12 + 20 + 8 + 22 + 15 + 8;

        /// <summary>
        /// What the CMYK container adds beyond <see cref="Jp2ContainerBytes"/>: EnumCS 12 is
        /// T.801's (Table M.25), so the file is a JPX — a fourth compatibility entry in ftyp
        /// (<c>jpxb</c>, 4) and a Reader Requirements box (T.801 M.11.1: 8 of header, then ML 1,
        /// FUAM 1, DCM 1, NSF 2, two features of 2 + 1, NVF 2 — 21).
        /// </summary>
        internal const int JpxContainerExtraBytes = 4 + 21;

        /// <summary>The container's bytes for <paramref name="colourChannels"/> colour channels: a JP2 for one or three, a JPX for four.</summary>
        internal static int ContainerBytes(int colourChannels) => Jp2ContainerBytes + (colourChannels == 4 ? JpxContainerExtraBytes : 0);

        /// <summary>What a Channel Definition box for <paramref name="components"/> channels adds: the box header, N, and six bytes a channel (Table I.16).</summary>
        internal static int CdefBoxBytes(int components) => 8 + 2 + 6 * components;

        /// <summary>Table A.14: the most layers an SGcod signals.</summary>
        internal const int MaxLayers = 65535;

        /// <summary><paramref name="value"/> halved <paramref name="times"/> times — a shift that does not wrap past 63.</summary>
        private static long Halve(long value, int times) => times >= 63 ? 0 : value >> times;

        /// <summary>
        /// Encodes <paramref name="planes"/> — one array per component, <paramref name="width"/> ×
        /// <paramref name="height"/> samples in raster order, each 0 to 2<sup>precision</sup> − 1, or
        /// −2<sup>precision − 1</sup> to 2<sup>precision − 1</sup> − 1 when <paramref name="isSigned"/> —
        /// losslessly. Returns a raw codestream (<c>FF 4F</c> …).
        /// </summary>
        internal static byte[] EncodeLossless(int[][] planes, int width, int height, int precision, bool isSigned, JpxEncodeOptions options) =>
            Encode(Widen(planes), width, height, precision, isSigned, options ?? new JpxEncodeOptions(), reversible: true);

        /// <summary>As <see cref="EncodeLossless(int[][], int, int, int, bool, JpxEncodeOptions)"/>, from <c>long</c> planes: samples of up to <see cref="MaxPrecision"/> bits.</summary>
        internal static byte[] EncodeLossless(long[][] planes, int width, int height, int precision, bool isSigned, JpxEncodeOptions options) =>
            Encode(planes, width, height, precision, isSigned, options ?? new JpxEncodeOptions(), reversible: true);

        /// <summary>
        /// Encodes <paramref name="planes"/> (as <see cref="EncodeLossless(long[][], int, int, int, bool, JpxEncodeOptions)"/> takes them) with the
        /// irreversible path at <see cref="JpxEncodeOptions.QuantizationStep"/>.
        /// </summary>
        internal static byte[] EncodeLossy(int[][] planes, int width, int height, int precision, bool isSigned, JpxEncodeOptions options) =>
            Encode(Widen(planes), width, height, precision, isSigned, options ?? new JpxEncodeOptions(), reversible: false);

        /// <summary>The irreversible path from <c>long</c> planes.</summary>
        internal static byte[] EncodeLossy(long[][] planes, int width, int height, int precision, bool isSigned, JpxEncodeOptions options) =>
            Encode(planes, width, height, precision, isSigned, options ?? new JpxEncodeOptions(), reversible: false);

        private static long[][] Widen(int[][] planes)
        {
            if (planes == null) return null;
            var wide = new long[planes.Length][];
            for (int c = 0; c < planes.Length; c++) wide[c] = planes[c] == null ? null : JpxForwardComponentTransform.Widen(planes[c]);
            return wide;
        }

        private static byte[] Encode(long[][] planes, int width, int height, int precision, bool isSigned, JpxEncodeOptions options, bool reversible)
        {
            Validate(planes, width, height, precision, isSigned, options, reversible);
            int components = planes.Length;
            int levels = options.Levels;
            bool mct = options.MultipleComponentTransform && components >= 3;

            // SIZ, read back through the one SIZ reader so that everything below is laid out as a decoder lays it out.
            byte[] siz = SizSegment(width, height, components, precision, isSigned, options);
            if (!JpxImageSize.TryRead(siz, 2, siz.Length, out JpxImageSize size, out string reason) || !size.TryValidateGrid(out reason))
                throw new ArgumentException(reason);

            byte[] precincts = PrecinctBytes(options);
            var component = new JpxComponentCoding(levels, options.CodeBlockWidthExponent, options.CodeBlockHeightExponent,
                options.HighThroughput ? JpxCodeBlockStyle.HighThroughput : JpxCodeBlockStyle.None, reversible, precincts);
            var cs = new JpxCodestream(new byte[0], size)
            {
                Main = { Coding = new JpxCodingDefault(options.ProgressionOrder, options.Layers, mct, options.Sop, options.Eph, component) },
            };

            // Each band's nominal range R_b (E-4), and its exponent and step: R_b itself without
            // quantisation (Table A.29), the nearest (ε, μ) to the requested step with it (E-3).
            int bandCount = 3 * levels + 1;
            var exponents = new int[bandCount];
            var mantissas = new int[bandCount];
            var steps = new double[bandCount];
            for (int r = 0; r <= levels; r++)
                for (int o = r == 0 ? 0 : 1; o <= (r == 0 ? 0 : 3); o++)
                {
                    int index = JpxQuantizationSteps.BandIndex(r, o);
                    int range = precision + Log2Gain(o);
                    if (reversible)
                    {
                        // A QCD's exponent has five bits (Table A.30): past 31 the band's planes are
                        // held by guard bits alone, up to the 37 of MaxGuardBits + 31 − 1 (E-2).
                        exponents[index] = Math.Min(range, JpxForwardQuantizer.MaxExponent);
                    }
                    else
                    {
                        JpxForwardQuantizer.StepSize(options.QuantizationStep, range, out exponents[index], out mantissas[index]);
                        steps[index] = JpxForwardQuantizer.Step(exponents[index], mantissas[index], range);
                    }
                }

            // The encode's workers (JpxParallel, as the decoder's): the tiles share them when there are
            // several, a tile's components when there is one, and then every code-block of every tile.
            // Each item writes only its own slot, so the codestream is the same at any degree.
            JpxParallel parallel = options.Workers ?? new JpxParallel(JpxParallel.Resolve(options.MaxThreads, (long)width * height * components));

            // Every tile-component through the level shift, the component transform, the wavelet and
            // (lossy) the quantiser: [tile][component][resolution][band] coefficients.
            int tiles = (int)size.NumTiles;
            var coefficients = new long[tiles][][][][];
            long[][][][] TileBands(int t, JpxParallel workers)
            {
                JpxRect tile = JpxGeometry.Tile(size, t);
                return reversible
                    ? ReversibleBands(planes, width, size, tile, precision, isSigned, mct, levels, workers)
                    : IrreversibleBands(planes, width, size, tile, precision, isSigned, mct, levels, steps, workers);
            }
            if (tiles > 1) parallel.For(tiles, (t, _) => coefficients[t] = TileBands(t, JpxParallel.Sequential));
            else coefficients[0] = TileBands(0, parallel);

            // Guard bits: M_b = G + ε_b − 1 (E-2) must hold every band's magnitude bit-planes, over
            // every tile and component (one QCD serves them all).
            var bandPlanes = new int[bandCount];
            foreach (long[][][][] tileBands in coefficients)
                foreach (long[][][] componentBands in tileBands)
                    for (int r = 0; r <= levels; r++)
                        for (int b = 0; b < componentBands[r].Length; b++)
                        {
                            int index = JpxQuantizationSteps.BandIndex(r, r == 0 ? 0 : b + 1);
                            bandPlanes[index] = Math.Max(bandPlanes[index], JpxPacketEncoder.BitLength(MaxMagnitude(componentBands[r][b])));
                        }
            int guard = GuardBitsFor(bandPlanes, exponents);
            if (guard > MaxGuardBits)
                throw new ArgumentException($"the samples' wavelet coefficients need {guard} guard bits; a QCD signals at most {MaxGuardBits} (T.800 Table A.28), " +
                    $"so a codestream holds {MaxCodedBitPlanes} magnitude bit-planes — samples this deep code irreversibly, or losslessly only when their coefficients stay within that", nameof(planes));
            cs.Main.Quantization = new JpxQuantization(reversible ? JpxQuantizationStyle.None : JpxQuantizationStyle.ScalarExpounded, guard, exponents, mantissas);

            // What a unit of a block's distortion is worth in the samples: the band's step squared
            // (1 without quantisation), its synthesis gain, and the component transformation's.
            double[][] gains = JpxSynthesisGain.Bands(levels, reversible);
            var weights = new double[components][][];
            for (int c = 0; c < components; c++)
            {
                double ct = mct && c < 3 ? JpxSynthesisGain.ComponentTransform(c, reversible) : 1;
                weights[c] = new double[levels + 1][];
                for (int r = 0; r <= levels; r++)
                {
                    weights[c][r] = new double[gains[r].Length];
                    for (int b = 0; b < gains[r].Length; b++)
                    {
                        double step = reversible ? 1 : steps[JpxQuantizationSteps.BandIndex(r, r == 0 ? 0 : b + 1)];
                        weights[c][r][b] = step * step * gains[r][b] * ct;
                    }
                }
            }

            // The codestream: main header, one tile-part per tile, EOC.
            var output = new List<byte>();
            AddU16(output, SOC);
            output.AddRange(siz);
            if (options.HighThroughput) output.AddRange(CapSegment(cs.Main.Quantization, levels, reversible));
            output.AddRange(CodSegment(options, levels, mct, reversible, precincts));
            output.AddRange(QcdSegment(cs.Main.Quantization));
            if (options.Comment != null) output.AddRange(ComSegment(options.Comment));

            // A size target is shared out by area, after the main header, the EOC and every
            // tile-part header; a tile's layers before the last get halving shares of the tile's.
            var budget = new WorkBudget(PdfLimits.MaxJpxEncodeWork, "JPEG 2000 encoding (PdfLimits.MaxJpxEncodeWork)");
            long imageArea = (long)width * height;
            long forPackets = options.TargetBytes > 0 ? Math.Max(0, options.TargetBytes - output.Count - 2 - TilePartHeaderBytes * tiles) : 0;
            var layouts = new JpxTileLayout[tiles];
            for (int t = 0; t < tiles; t++)
                if (!JpxTileLayout.TryBuild(cs, t, out layouts[t], out reason)) throw new ArgumentException(reason, nameof(planes));
            // Tier 1 on every code-block of every tile in one pass over the workers. HT blocks are coded
            // at every plane and allocated by plane (JpxHtRateControl); Part 1 blocks are coded whole and
            // truncated (JpxRateControl).
            List<BlockJob> jobs = BlockJobs(layouts, coefficients, cs.Main.Quantization, weights);
            Dictionary<JpxCodeBlock, JpxEncodedBlock>[] tileBlocks = null;
            Dictionary<JpxCodeBlock, JpxHtRateControl.Candidates>[] tileCandidates = null;
            if (options.HighThroughput) tileCandidates = HtCandidates(jobs, tiles, options, parallel);
            else tileBlocks = EncodeBlocks(jobs, tiles, options.Layers, options.VerticallyCausal, parallel);
            // The coefficients are coded: what is left needs the blocks alone, and a tile's go once it is written.
            jobs.Clear();
            Array.Clear(coefficients, 0, tiles);
            for (int t = 0; t < tiles; t++)
            {
                JpxTileLayout layout = layouts[t];
                Dictionary<JpxCodeBlock, JpxEncodedBlock> blocks = tileBlocks?[t];
                Dictionary<JpxCodeBlock, JpxHtRateControl.Candidates> candidates = tileCandidates?[t];
                if (tileBlocks != null) tileBlocks[t] = null;
                if (tileCandidates != null) tileCandidates[t] = null;
                List<byte> Allocate(long[] layerTargets) => options.HighThroughput
                    ? JpxHtRateControl.Allocate(layout, candidates, layerTargets, options.Sop, options.Eph, budget)
                    : JpxRateControl.Allocate(layout, blocks, layerTargets, options.Sop, options.Eph, budget);

                var targets = new long[options.Layers];
                if (options.TargetBytes > 0)
                {
                    JpxRect tile = JpxGeometry.Tile(size, t);
                    long share = SaturatingCast.ToLong((double)forPackets * (tile.Width * tile.Height) / imageArea);
                    for (int l = 0; l < targets.Length; l++) targets[l] = Math.Max(1, Halve(share, targets.Length - 1 - l));
                }
                else if (options.Layers > 1)
                {
                    // Without a target the layers split the whole by halves: measure the whole first.
                    List<byte> whole = Allocate(new long[options.Layers]);
                    for (int l = 0; l < targets.Length - 1; l++) targets[l] = Math.Max(1, Halve(whole.Count, targets.Length - 1 - l));
                }
                List<byte> tileData = Allocate(targets);

                // SOT (Table A.5): Lsot 10, Isot, Psot from the SOT's first byte to the data's end, TPsot 0, TNsot 1.
                AddU16(output, SOT);
                AddU16(output, 10);
                AddU16(output, t);
                AddU32(output, TilePartHeaderBytes + tileData.Count);
                output.Add(0);
                output.Add(1);
                AddU16(output, SOD);
                output.AddRange(tileData);
            }
            AddU16(output, EOC);
            return output.ToArray();
        }

        /// <summary>
        /// Wraps a raw codestream in the JP2 file format (T.800 Annex I): the signature box, a File
        /// Type box branded <c>jp2 </c>, a JP2 Header box with the Image Header and a Colour
        /// Specification naming greyscale (one component) or sRGB (three, Table I.10) — or CMYK
        /// (four; EnumCS 12, T.801 Table M.24, which ISO 32000-2 §7.4.9 admits) — then the
        /// Contiguous Codestream box. A Bits Per Component box is added when the components differ.
        /// With <paramref name="lastChannelIsOpacity"/> the last component is the image's opacity
        /// (two, four or five components) and a Channel Definition box says so (I.5.3.6): the
        /// colour channels typed 0 with their colour's number, the last typed 1 for the whole image
        /// — what a PDF's <c>/SMaskInData 1</c> lets a viewer use.
        /// </summary>
        internal static byte[] WrapAsJp2(byte[] codestream, bool lastChannelIsOpacity = false)
        {
            if (!JpxHeader.IsRawCodestream(codestream) || codestream.Length < 6 || codestream[2] != 0xFF || codestream[3] != 0x51)
                throw new ArgumentException("not a raw JPEG 2000 codestream: SOC and SIZ must open it (T.800 §A.4.1)", nameof(codestream));
            if (!JpxImageSize.TryRead(codestream, 4, codestream.Length, out JpxImageSize size, out string reason))
                throw new ArgumentException(reason, nameof(codestream));
            int components = size.Components.Length;
            int colour = lastChannelIsOpacity ? components - 1 : components;
            if (colour != 1 && colour != 3 && colour != 4)
                throw new ArgumentException($"the codestream has {components} components{(lastChannelIsOpacity ? ", the last opacity" : "")}; a JP2 colour specification is written here for one (greyscale), three (sRGB) or four (CMYK) colour channels only", nameof(codestream));

            bool uniform = true;
            foreach (JpxComponentSize c in size.Components)
                uniform &= c.Precision == size.Components[0].Precision && c.IsSigned == size.Components[0].IsSigned;

            var ihdr = new List<byte>();
            AddU32(ihdr, size.Ysiz - size.YOsiz);
            AddU32(ihdr, size.Xsiz - size.XOsiz);
            AddU16(ihdr, components);
            ihdr.Add(uniform ? Bpc(size.Components[0]) : (byte)255);
            ihdr.Add(7);     // C: the compression type, always 7 (Table I.5)
            ihdr.Add(0);     // UnkC: the colourspace is known
            ihdr.Add(0);     // IPR: no intellectual property box

            var header = new List<byte>();
            header.AddRange(Box("ihdr", ihdr.ToArray()));
            if (!uniform)
            {
                var bpcc = new List<byte>();
                foreach (JpxComponentSize c in size.Components) bpcc.Add(Bpc(c));
                header.AddRange(Box("bpcc", bpcc.ToArray()));
            }
            var colr = new List<byte> { 1, 0, 0 };   // METH 1 (enumerated), PREC 0, APPROX 0
            AddU32(colr, colour == 1 ? 17 : colour == 3 ? 16 : 12);
            header.AddRange(Box("colr", colr.ToArray()));
            if (lastChannelIsOpacity)
            {
                // cdef (Table I.16): N, then per channel its index, type (0 colour, 1 opacity) and
                // association (the colour's number from 1, or 0 for the whole image).
                var cdef = new List<byte>();
                AddU16(cdef, components);
                for (int c = 0; c < colour; c++)
                {
                    AddU16(cdef, c);
                    AddU16(cdef, 0);
                    AddU16(cdef, c + 1);
                }
                AddU16(cdef, colour);
                AddU16(cdef, 1);
                AddU16(cdef, 0);
                header.AddRange(Box("cdef", cdef.ToArray()));
            }

            // T.800 Table I.10 (sRGB, greyscale, and a JPX CMYK alike) and Table I.16 (an opacity
            // channel) are defined over unsigned channels; a signed codestream stays a raw codestream.
            foreach (JpxComponentSize c in size.Components)
                if (c.IsSigned)
                    throw new ArgumentException("the codestream has signed components; a JP2's colour specification and channel definitions describe unsigned channels only (T.800 Tables I.10 and I.16) — write the raw codestream instead", nameof(codestream));

            // A JP2 names sRGB, greyscale and sYCC only (T.800 I.5.3.3); EnumCS 12, CMYK, is
            // T.801's, so that file is a JPX: brand 'jpx ', compatible with 'jp2 ' (a Part 1 reader
            // may try it) and 'jpxb' (the baseline, M.9.2), and a Reader Requirements box right
            // after ftyp (M.11.1) naming what it needs — a Part 1 codestream and the CMYK
            // colourspace, both required to display and to fully understand the file.
            bool jpx = colour == 4;

            // An HTJ2K codestream (Rsiz bit 14) goes in a JPH file, not a JP2 (T.814 Annex D): brand
            // 'jph ', MinV 0, 'jph ' among the compatibility list — and not 'jp2 ', since a Part 1
            // reader cannot decode HT code-blocks. JPH enumerates no CMYK, so that file is refused.
            bool jph = (size.Rsiz & RsizHighThroughput) != 0;
            if (jph && jpx)
                throw new ArgumentException("an HTJ2K codestream goes in a JPH file (T.814 Annex D), whose colour specification has no CMYK enumeration; write the raw codestream instead", nameof(codestream));

            var file = new List<byte> { 0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A };
            var ftyp = new List<byte>();
            ftyp.AddRange(Ascii(jph ? "jph " : jpx ? "jpx " : "jp2 "));
            AddU32(ftyp, 0);
            ftyp.AddRange(Ascii(jph ? "jph " : "jp2 "));
            if (jpx) ftyp.AddRange(Ascii("jpxb"));
            file.AddRange(Box("ftyp", ftyp.ToArray()));
            if (jpx) file.AddRange(Box("rreq", ReaderRequirements()));
            file.AddRange(Box("jp2h", header.ToArray()));
            file.AddRange(Box("jp2c", codestream));
            return file.ToArray();
        }

        /// <summary>T.801 Table M.14: a codestream with no extensions is feature 1, the CMYK enumerated colourspace feature 55.</summary>
        internal const int FeatureCodestreamNoExtensions = 1, FeatureCmykColourspace = 55;

        /// <summary>
        /// The Reader Requirements box for the CMYK JPX (T.801 M.11.1, Table M.15): a one-byte
        /// mask; the two sub-expressions both needed to fully understand (FUAM) and to display
        /// (DCM) the file; two standard features, each satisfying its own sub-expression; no
        /// vendor features.
        /// </summary>
        private static byte[] ReaderRequirements()
        {
            var rreq = new List<byte> { 1, 0xC0, 0xC0 };
            AddU16(rreq, 2);
            AddU16(rreq, FeatureCodestreamNoExtensions);
            rreq.Add(0x80);
            AddU16(rreq, FeatureCmykColourspace);
            rreq.Add(0x40);
            AddU16(rreq, 0);
            return rreq.ToArray();
        }

        /// <summary>The smallest number of guard bits, at least <see cref="DefaultGuardBits"/>, for which every band's planes fit M<sub>b</sub> (E-2).</summary>
        internal static int GuardBitsFor(int[] bandPlanes, int[] exponents)
        {
            int guard = DefaultGuardBits;
            for (int i = 0; i < bandPlanes.Length; i++) guard = Math.Max(guard, bandPlanes[i] - exponents[i] + 1);
            return guard;
        }

        private static void Validate(long[][] planes, int width, int height, int precision, bool isSigned, JpxEncodeOptions options, bool reversible)
        {
            if (planes == null || planes.Length < 1) throw new ArgumentException("at least one component plane is needed", nameof(planes));
            if (planes.Length > JpxImageSize.MaxComponents) throw new ArgumentException($"{planes.Length} components; T.800 allows at most {JpxImageSize.MaxComponents}", nameof(planes));
            if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width), "the image must have at least one sample");
            if (precision < 1 || precision > MaxPrecision) throw new ArgumentOutOfRangeException(nameof(precision), $"samples of 1 to {MaxPrecision} bits are encoded");
            long count = (long)width * height;
            long low = isSigned ? -(1L << (precision - 1)) : 0;
            long high = isSigned ? (1L << (precision - 1)) - 1 : (1L << precision) - 1;
            for (int c = 0; c < planes.Length; c++)
            {
                if (planes[c] == null || planes[c].Length != count)
                    throw new ArgumentException($"component {c} must hold {count} samples, one per pixel in raster order", nameof(planes));
                for (int i = 0; i < planes[c].Length; i++)
                    if (planes[c][i] < low || planes[c][i] > high)
                        throw new ArgumentOutOfRangeException(nameof(planes), $"component {c}'s sample {i} is {planes[c][i]}, outside {low} to {high} for {precision}-bit {(isSigned ? "signed" : "unsigned")} samples");
            }
            if (options.Levels < 0 || options.Levels > 32) throw new ArgumentOutOfRangeException(nameof(options), "T.800 Table A.15 allows 0 to 32 decomposition levels");
            int xcb = options.CodeBlockWidthExponent, ycb = options.CodeBlockHeightExponent;
            if (xcb < 2 || xcb > 10 || ycb < 2 || ycb > 10 || xcb + ycb > 12)
                throw new ArgumentOutOfRangeException(nameof(options), "T.800 Table A.18 allows code-block exponents of 2 to 10 whose sum is at most 12");
            if (!reversible && (!(options.QuantizationStep > 0) || double.IsInfinity(options.QuantizationStep)))
                throw new ArgumentOutOfRangeException(nameof(options), "the quantisation step is a positive finite number");
            if (options.Precincts != null)
            {
                if (options.Precincts.Length == 0) throw new ArgumentException("precincts need at least one (PPx, PPy) entry", nameof(options));
                for (int r = 0; r < options.Precincts.Length; r++)
                {
                    (int px, int py) = options.Precincts[r];
                    if (px < 0 || px > 15 || py < 0 || py > 15 || (r > 0 && (px == 0 || py == 0)))
                        throw new ArgumentOutOfRangeException(nameof(options), "T.800 Table A.21 allows precinct exponents of 0 to 15, and 0 only at resolution level 0");
                }
            }
            if (options.TileWidth < 0 || options.TileHeight < 0 || (options.TileWidth == 0) != (options.TileHeight == 0))
                throw new ArgumentOutOfRangeException(nameof(options), "a tile size is two positive extents, or 0 and 0 for one tile");
            if (options.Layers < 1 || options.Layers > MaxLayers)
                throw new ArgumentOutOfRangeException(nameof(options), $"T.800 Table A.14 allows 1 to {MaxLayers} quality layers");
            if (options.TargetBytes < 0)
                throw new ArgumentOutOfRangeException(nameof(options), "a size target is a number of bytes, or 0 for none");
        }

        /// <summary>Table E.1's log<sub>2</sub> gain by orientation — <see cref="JpxDequantizer.Log2Gain"/>, the one statement, so R<sub>b</sub> is the same number on both sides of the codestream.</summary>
        internal static int Log2Gain(int orientation) => JpxDequantizer.Log2Gain(orientation);

        /// <summary>The tile's part of component <paramref name="c"/> as an int array, level shifted (G-1).</summary>
        private static long[] TileSamples(long[][] planes, int imageWidth, JpxImageSize size, JpxRect tile, int c, int precision, bool isSigned)
        {
            JpxRect tc = JpxGeometry.TileComponent(size, tile, c);
            int w = (int)tc.Width, h = (int)tc.Height;
            var samples = new long[w * h];
            for (int y = 0; y < h; y++)
                Array.Copy(planes[c], (int)(tc.Y0 + y) * imageWidth + (int)tc.X0, samples, y * w, w);
            JpxForwardComponentTransform.DcShift(samples, precision, isSigned);
            return samples;
        }

        private static long[][][][] ReversibleBands(long[][] planes, int imageWidth, JpxImageSize size, JpxRect tile,
            int precision, bool isSigned, bool mct, int levels, JpxParallel parallel)
        {
            var work = new long[planes.Length][];
            for (int c = 0; c < work.Length; c++) work[c] = TileSamples(planes, imageWidth, size, tile, c, precision, isSigned);
            if (mct) JpxForwardComponentTransform.Rct(work[0], work[1], work[2]);

            var bands = new long[planes.Length][][][];
            parallel.For(work.Length, (c, _) =>
                bands[c] = JpxForwardWavelet.Reversible(work[c], JpxGeometry.TileComponent(size, tile, c), levels));
            return bands;
        }

        private static long[][][][] IrreversibleBands(long[][] planes, int imageWidth, JpxImageSize size, JpxRect tile,
            int precision, bool isSigned, bool mct, int levels, double[] steps, JpxParallel parallel)
        {
            var work = new double[planes.Length][];
            for (int c = 0; c < work.Length; c++)
            {
                long[] samples = TileSamples(planes, imageWidth, size, tile, c, precision, isSigned);
                work[c] = new double[samples.Length];
                for (int i = 0; i < samples.Length; i++) work[c][i] = samples[i];
            }
            if (mct) JpxForwardComponentTransform.Ict(work[0], work[1], work[2]);

            var bands = new long[planes.Length][][][];
            parallel.For(work.Length, (c, _) =>
            {
                double[][][] real = JpxForwardWavelet.Irreversible(work[c], JpxGeometry.TileComponent(size, tile, c), levels);
                var quantized = new long[real.Length][][];
                for (int r = 0; r < real.Length; r++)
                {
                    quantized[r] = new long[real[r].Length][];
                    for (int b = 0; b < real[r].Length; b++)
                    {
                        double step = steps[JpxQuantizationSteps.BandIndex(r, r == 0 ? 0 : b + 1)];
                        var q = new long[real[r][b].Length];
                        for (int i = 0; i < q.Length; i++) q[i] = JpxForwardQuantizer.Quantize(real[r][b][i], step);
                        quantized[r][b] = q;
                    }
                }
                bands[c] = quantized;
            });
            return bands;
        }

        /// <summary>One code-block to code: its tile, where its coefficients lie in its band's, and what its band says of it.</summary>
        private readonly struct BlockJob
        {
            internal BlockJob(int tile, JpxCodeBlock block, long[] band, JpxRect bandBounds, int orientation, int magnitudeBitPlanes, double weight)
            {
                Tile = tile;
                Block = block;
                Band = band;
                BandBounds = bandBounds;
                Orientation = orientation;
                MagnitudeBitPlanes = magnitudeBitPlanes;
                Weight = weight;
            }

            internal int Tile { get; }
            internal JpxCodeBlock Block { get; }
            internal long[] Band { get; }
            internal JpxRect BandBounds { get; }
            internal int Orientation { get; }
            internal int MagnitudeBitPlanes { get; }
            internal double Weight { get; }

            /// <summary>The block's coefficients, copied out of its band in raster order.</summary>
            internal long[] Samples(out int w, out int h)
            {
                JpxRect bounds = Block.Bounds;
                w = (int)bounds.Width;
                h = (int)bounds.Height;
                int bandWidth = (int)BandBounds.Width;
                var samples = new long[w * h];
                for (int y = 0; y < h; y++)
                    Array.Copy(Band, (int)(bounds.Y0 - BandBounds.Y0 + y) * bandWidth + (int)(bounds.X0 - BandBounds.X0), samples, y * w, w);
                return samples;
            }
        }

        /// <summary>
        /// Every code-block of every tile, in the order the tile dictionaries were always filled — tile, component,
        /// resolution, band, precinct, block — each with its band's coefficients, M<sub>b</sub> and <paramref name="weights"/>.
        /// </summary>
        private static List<BlockJob> BlockJobs(JpxTileLayout[] layouts, long[][][][][] coefficients, JpxQuantization quantization, double[][][] weights)
        {
            var jobs = new List<BlockJob>();
            for (int t = 0; t < layouts.Length; t++)
            {
                JpxTileLayout layout = layouts[t];
                for (int c = 0; c < layout.Components.Length; c++)
                {
                    JpxTileComponent component = layout.Components[c];
                    for (int r = 0; r < component.Resolutions.Length; r++)
                    {
                        JpxResolution resolution = component.Resolutions[r];
                        for (int b = 0; b < resolution.Bands.Length; b++)
                        {
                            JpxBand band = resolution.Bands[b];
                            long[] data = coefficients[t][c][r][b];
                            if (data.Length != band.Bounds.Width * band.Bounds.Height)
                                throw new InvalidOperationException($"the wavelet's band {band.Orientation} at resolution {r} holds {data.Length} coefficients; the layout's spans {band.Bounds}");
                            int mb = JpxQuantizationSteps.MagnitudeBitPlanes(quantization, component.Levels, r, band.Orientation);
                            foreach (JpxPrecinct precinct in resolution.Precincts)
                                foreach (JpxCodeBlock block in precinct.Bands[b].Blocks)
                                    jobs.Add(new BlockJob(t, block, data, band.Bounds, band.Orientation, mb, weights[c][r][b]));
                        }
                    }
                }
            }
            return jobs;
        }

        /// <summary>
        /// HT tier 1 on every code-block: each block's sets, with the error they leave weighed by its band's
        /// weight — what <see cref="JpxHtRateControl"/> chooses from — one dictionary per tile. Without a size
        /// target only the whole block's set is ever chosen, so only it is coded.
        /// </summary>
        private static Dictionary<JpxCodeBlock, JpxHtRateControl.Candidates>[] HtCandidates(List<BlockJob> jobs, int tiles,
            JpxEncodeOptions options, JpxParallel parallel)
        {
            var sets = options.TargetBytes <= 0 ? JpxHtRateControl.SetsCoded.WholeOnly
                : options.HtRefinementPasses ? JpxHtRateControl.SetsCoded.All : JpxHtRateControl.SetsCoded.CleanupOnly;
            var coded = new JpxHtRateControl.Candidates[jobs.Count];
            parallel.For(jobs.Count, (i, _) =>
            {
                BlockJob job = jobs[i];
                long[] samples = job.Samples(out int w, out int h);
                coded[i] = JpxHtRateControl.Encode(samples, w, h, job.MagnitudeBitPlanes, job.Weight, sets, options.VerticallyCausal);
            });
            var candidates = new Dictionary<JpxCodeBlock, JpxHtRateControl.Candidates>[tiles];
            for (int t = 0; t < tiles; t++) candidates[t] = new Dictionary<JpxCodeBlock, JpxHtRateControl.Candidates>();
            for (int i = 0; i < jobs.Count; i++) candidates[jobs[i].Tile].Add(jobs[i].Block, coded[i]);
            return candidates;
        }

        /// <summary>Tier 1 on every code-block, keyed by the layout's block, each weighed by its band's weight — one dictionary per tile.</summary>
        private static Dictionary<JpxCodeBlock, JpxEncodedBlock>[] EncodeBlocks(List<BlockJob> jobs, int tiles, int layers, bool verticallyCausal, JpxParallel parallel)
        {
            var coded = new JpxEncodedBlock[jobs.Count];
            parallel.For(jobs.Count, (i, _) =>
            {
                BlockJob job = jobs[i];
                long[] samples = job.Samples(out int w, out int h);
                JpxBlockCode code = JpxBlockEncoder.Encode(samples, w, h, job.Orientation, verticallyCausal);
                coded[i] = new JpxEncodedBlock(code, job.MagnitudeBitPlanes - code.Planes, layers, job.Weight);
            });
            var blocks = new Dictionary<JpxCodeBlock, JpxEncodedBlock>[tiles];
            for (int t = 0; t < tiles; t++) blocks[t] = new Dictionary<JpxCodeBlock, JpxEncodedBlock>();
            for (int i = 0; i < jobs.Count; i++) blocks[jobs[i].Tile].Add(jobs[i].Block, coded[i]);
            return blocks;
        }

        private static long MaxMagnitude(long[] values)
        {
            long max = 0;
            foreach (long v in values) max = Math.Max(max, Math.Abs(v));
            return max;
        }

        private static byte Bpc(JpxComponentSize c) => (byte)((c.Precision - 1) | (c.IsSigned ? 0x80 : 0));

        /// <summary>SIZ (Table A.9): Rsiz 0 — or bit 14 for HTJ2K (T.814 §A.2) — the image at the grid's origin, the tiling, one Ssiz/XRsiz/YRsiz per component.</summary>
        private static byte[] SizSegment(int width, int height, int components, int precision, bool isSigned, JpxEncodeOptions options)
        {
            var p = new List<byte>();
            AddU16(p, options.HighThroughput ? RsizHighThroughput : 0);
            AddU32(p, width);
            AddU32(p, height);
            AddU32(p, 0);
            AddU32(p, 0);
            AddU32(p, options.TileWidth > 0 ? options.TileWidth : width);
            AddU32(p, options.TileHeight > 0 ? options.TileHeight : height);
            AddU32(p, 0);
            AddU32(p, 0);
            AddU16(p, components);
            for (int c = 0; c < components; c++)
            {
                p.Add((byte)((precision - 1) | (isSigned ? 0x80 : 0)));
                p.Add(1);
                p.Add(1);
            }
            return Segment(SIZ, p.ToArray());
        }

        /// <summary>COD (Tables A.12 to A.21): Scod, SGcod (progression, the layers, the transform flag), SPcod.</summary>
        private static byte[] CodSegment(JpxEncodeOptions options, int levels, bool mct, bool reversible, byte[] precincts)
        {
            var p = new List<byte>
            {
                (byte)((precincts != null ? 0x01 : 0) | (options.Sop ? 0x02 : 0) | (options.Eph ? 0x04 : 0)),
                (byte)options.ProgressionOrder,
            };
            AddU16(p, options.Layers);
            p.Add((byte)(mct ? 1 : 0));
            p.Add((byte)levels);
            p.Add((byte)(options.CodeBlockWidthExponent - 2));
            p.Add((byte)(options.CodeBlockHeightExponent - 2));
            p.Add((byte)((options.HighThroughput ? JpxCodeBlockStyle.HighThroughput : JpxCodeBlockStyle.None)
                | (options.VerticallyCausal ? JpxCodeBlockStyle.VerticallyCausal : JpxCodeBlockStyle.None)));
            p.Add((byte)(reversible ? 1 : 0));
            if (precincts != null) p.AddRange(precincts);
            return Segment(COD, p.ToArray());
        }

        /// <summary>
        /// CAP (T.800 Table A.11bis, T.814 §A.3): Pcap with Pcap15 alone, then Ccap15 — HTONLY (bits 15
        /// and 14 zero, §A.3.2), one HT set per block (bit 13 zero, §A.3.3 — layers decide when a block
        /// arrives, never re-code it; see JpxHtRateControl), no RGN (bit 12), homogeneous
        /// (bit 11), bit 5 when the transform is irreversible (§A.3.6), and in bits 0 to 4 the smallest P
        /// whose bound B (§A.3.7) covers every band's M<sub>b</sub>: no cleanup magnitude reaches 2<sup>M<sub>b</sub></sup>.
        /// </summary>
        private static byte[] CapSegment(JpxQuantization quantization, int levels, bool reversible)
        {
            int planes = 0;
            for (int r = 0; r <= levels; r++)
                for (int o = r == 0 ? 0 : 1; o <= (r == 0 ? 0 : 3); o++)
                    planes = Math.Max(planes, JpxQuantizationSteps.MagnitudeBitPlanes(quantization, levels, r, o));
            int ccap = (reversible ? 0 : 0x20) | MagnitudeBoundParameter(planes);
            var p = new List<byte>();
            AddU32(p, (int)Pcap15);
            AddU16(p, ccap);
            return Segment(CAP, p.ToArray());
        }

        /// <summary>
        /// The smallest P (Ccap15 bits 0 to 4) whose B ≥ <paramref name="planes"/>: B is 8 at P = 0, P + 8
        /// below 20, 4(P − 19) + 27 from 20 to 30, and 74 at 31 (T.814 §A.3.7).
        /// </summary>
        internal static int MagnitudeBoundParameter(int planes)
        {
            if (planes <= 8) return 0;
            if (planes <= 27) return planes - 8;
            if (planes <= 4 * (30 - 19) + 27) return 19 + (planes - 27 + 3) / 4;
            return 31;
        }

        /// <summary>Table A.21's byte per resolution level, PPy in the high nibble; null without signalled precincts.</summary>
        private static byte[] PrecinctBytes(JpxEncodeOptions options)
        {
            if (options.Precincts == null) return null;
            var bytes = new byte[options.Levels + 1];
            for (int r = 0; r <= options.Levels; r++)
            {
                (int px, int py) = options.Precincts[Math.Min(r, options.Precincts.Length - 1)];
                bytes[r] = (byte)((py << 4) | px);
            }
            return bytes;
        }

        /// <summary>QCD (Tables A.27 to A.30): Sqcd from the guard bits and style, then one entry per sub-band in Table A.30's order.</summary>
        private static byte[] QcdSegment(JpxQuantization q)
        {
            var p = new List<byte> { (byte)((q.GuardBits << 5) | (int)q.Style) };
            for (int i = 0; i < q.Exponents.Length; i++)
            {
                if (q.Style == JpxQuantizationStyle.None) p.Add((byte)(q.Exponents[i] << 3));
                else AddU16(p, (q.Exponents[i] << 11) | q.Mantissas[i]);
            }
            return Segment(QCD, p.ToArray());
        }

        /// <summary>COM (Table A.43) with Rcom 1: Latin text, a character past 0xFF written as '?'.</summary>
        private static byte[] ComSegment(string text)
        {
            var p = new List<byte>();
            AddU16(p, 1);
            foreach (char ch in text) p.Add(ch <= 0xFF ? (byte)ch : (byte)'?');
            return Segment(COM, p.ToArray());
        }

        private static byte[] Segment(int marker, byte[] payload)
        {
            int length = payload.Length + 2;
            if (length > 0xFFFF) throw new ArgumentException("a marker segment's payload is at most 65 533 bytes", nameof(payload));
            var s = new List<byte>();
            AddU16(s, marker);
            AddU16(s, length);
            s.AddRange(payload);
            return s.ToArray();
        }

        private static byte[] Box(string type, byte[] payload)
        {
            var box = new List<byte>();
            AddU32(box, 8 + payload.Length);
            box.AddRange(Ascii(type));
            box.AddRange(payload);
            return box.ToArray();
        }

        private static byte[] Ascii(string s)
        {
            var bytes = new byte[s.Length];
            for (int i = 0; i < s.Length; i++) bytes[i] = (byte)s[i];
            return bytes;
        }

        private static void AddU16(List<byte> to, int v) => IO.BigEndian.Add16(to, v);

        private static void AddU32(List<byte> to, long v) => IO.BigEndian.Add32(to, unchecked((uint)v));
    }
}
