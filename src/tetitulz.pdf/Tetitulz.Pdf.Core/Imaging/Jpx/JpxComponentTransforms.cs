// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>
    /// What follows the inverse wavelet (T.800 Annex G): the inverse RCT on integers (G-6 to G-8),
    /// the inverse ICT in double (G-12 to G-14), rounding to samples, the inverse DC level shift
    /// (G-2) and the clip to the component's range. Beside them, the two Part 2 steps the JPX
    /// baseline admits (T.801 §M.9.2.3): <see cref="JpxMultipleComponentTransform"/> takes the place
    /// of the RCT/ICT and <see cref="JpxNonLinearity"/> follows the level shift.
    /// </summary>
    /// <remarks>
    /// <para><b>Order</b> (G.1.2, T.801 Annex J and Figure K.1): inverse wavelet → inverse RCT/ICT
    /// (components 0–2, when the COD says so) <i>or</i> the Part 2 transformation (when an MCO
    /// names a stage) → round to integers → inverse DC level shift for unsigned components
    /// (<b>not</b> after a Part 2 transformation, whose offset array replaces it — Annex J's
    /// opening paragraph) → clip → non-linearity → the image.</para>
    /// <para>The integer steps run on the <c>double</c> planes the wavelet produced — exact, since
    /// every value is an integer below 2<sup>53</sup> — and the samples are stored once at the end,
    /// into an <c>int</c> plane up to 31 bits and a <c>long</c> plane above (T.800 allows 38). A
    /// component deeper than <see cref="MaxSamplePrecision"/> bits has no room, and
    /// <see cref="JpxDecodedImage.TryCreate"/> refuses it.</para>
    /// </remarks>
    internal static class JpxComponentTransforms
    {
        /// <summary>The deepest component a sample plane holds: T.800's 38 bits (Table A.11).</summary>
        internal const int MaxSamplePrecision = 38;

        /// <summary>The deepest component an <c>int</c> plane holds: 31 bits unsigned; deeper ones take a <c>long</c> plane.</summary>
        internal const int MaxNarrowPrecision = 31;

        /// <summary>Whether a component of <paramref name="precision"/> bits takes a <c>long</c> plane.</summary>
        internal static bool IsWide(int precision) => precision > MaxNarrowPrecision;

        /// <summary>
        /// G.2 / G.3: the RCT and ICT apply to components 0, 1 and 2, which must exist and share their
        /// separation on the reference grid and their bit depth. False with the reason otherwise.
        /// </summary>
        internal static bool CanTransformFirstThree(JpxImageSize siz, out string reason)
        {
            reason = null;
            if (siz.Components.Length < 3)
            {
                reason = $"the component transformation needs components 0-2 of a {siz.Components.Length}-component image (T.800 G.2)";
                return false;
            }
            JpxComponentSize first = siz.Components[0];
            for (int c = 1; c < 3; c++)
            {
                JpxComponentSize other = siz.Components[c];
                if (other.XRsiz != first.XRsiz || other.YRsiz != first.YRsiz)
                {
                    reason = $"component {c} is sub-sampled {other.XRsiz}x{other.YRsiz} but component 0 {first.XRsiz}x{first.YRsiz}; the component transformation needs the same separation (T.800 G.2)";
                    return false;
                }
                if (other.Precision != first.Precision)
                {
                    reason = $"component {c} has {other.Precision}-bit samples but component 0 {first.Precision}-bit; the component transformation needs the same bit depth (T.800 G.2)";
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// The inverse RCT (T.800 G-6 to G-8), in place and exact: on entry the arrays hold Y0, Y1, Y2;
        /// on return I0, I1, I2 — with I1 = Y0 − ⌊(Y2 + Y1) / 4⌋, I0 = Y2 + I1, I2 = Y1 + I1. With RGB
        /// input to the forward transform, I0 is red, I1 green and I2 blue: Y1 was B − G and Y2 was R − G.
        /// </summary>
        internal static void InverseRct(double[] y0, double[] y1, double[] y2)
        {
            RequireSameLength(y0.Length, y1.Length, y2.Length);
            for (int i = 0; i < y0.Length; i++)
            {
                // The values are integers (rounded first), so the floor is exact: a truncating cast would round a negative sum the wrong way.
                double a = y1[i], b = y2[i];
                double g = y0[i] - Math.Floor((a + b) / 4);
                y0[i] = b + g;
                y1[i] = g;
                y2[i] = a + g;
            }
        }

        /// <summary>The inverse RCT on integer arrays: the <c>double</c> one over copies, for callers that hold integers.</summary>
        internal static void InverseRct(int[] y0, int[] y1, int[] y2)
        {
            double[] d0 = ToDoubles(y0), d1 = ToDoubles(y1), d2 = ToDoubles(y2);
            InverseRct(d0, d1, d2);
            for (int i = 0; i < y0.Length; i++)
            {
                y0[i] = SaturatingCast.ToInt(d0[i]);
                y1[i] = SaturatingCast.ToInt(d1[i]);
                y2[i] = SaturatingCast.ToInt(d2[i]);
            }
        }

        private static double[] ToDoubles(int[] values)
        {
            var d = new double[values.Length];
            for (int i = 0; i < values.Length; i++) d[i] = values[i];
            return d;
        }

        /// <summary>
        /// The inverse ICT (T.800 G-12 to G-14), in place: on entry Y, Cb, Cr; on return
        /// I0 = Y + 1.402 Cr, I1 = Y − 0.34413 Cb − 0.71414 Cr, I2 = Y + 1.772 Cb.
        /// </summary>
        internal static void InverseIct(double[] y0, double[] y1, double[] y2)
        {
            RequireSameLength(y0.Length, y1.Length, y2.Length);
            for (int i = 0; i < y0.Length; i++)
            {
                double y = y0[i], cb = y1[i], cr = y2[i];
                y0[i] = y + 1.402 * cr;
                y1[i] = y - 0.34413 * cb - 0.71414 * cr;
                y2[i] = y + 1.772 * cb;
            }
        }

        /// <summary>Rounds reconstructed values to integer samples, half away from zero, saturating at the <c>int</c> range.</summary>
        internal static void RoundToSamples(double[] source, int[] samples)
        {
            if (samples.Length < source.Length)
                throw new ArgumentException("the sample array is shorter than the source", nameof(samples));
            for (int i = 0; i < source.Length; i++) samples[i] = RoundAndClamp(source[i], int.MinValue, int.MaxValue);
        }

        /// <summary>Rounds every value in place, half away from zero (NaN to 0): integers in <c>double</c>, exact below 2^53 — the form the steps after the wavelet run on.</summary>
        internal static void RoundInPlace(double[] values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                double v = values[i];
                values[i] = double.IsNaN(v) ? 0 : Math.Round(v, MidpointRounding.AwayFromZero);
            }
        }

        /// <summary>Stores rounded, clipped values into an <c>int</c> plane; the values fit by the clip before them.</summary>
        internal static void Store(double[] values, int[] samples)
        {
            if (samples.Length < values.Length) throw new ArgumentException("the sample array is shorter than the source", nameof(samples));
            for (int i = 0; i < values.Length; i++) samples[i] = SaturatingCast.ToInt(values[i]);
        }

        /// <summary>Stores rounded, clipped values into a <c>long</c> plane, for a component deeper than <see cref="MaxNarrowPrecision"/>.</summary>
        internal static void Store(double[] values, long[] samples)
        {
            if (samples.Length < values.Length) throw new ArgumentException("the sample array is shorter than the source", nameof(samples));
            for (int i = 0; i < values.Length; i++) samples[i] = SaturatingCast.ToLong(values[i]);
        }

        /// <summary>The one rounding rule: half away from zero, then clipped to [<paramref name="min"/>, <paramref name="max"/>]; NaN counts as 0.</summary>
        internal static int RoundAndClamp(double value, int min, int max)
        {
            if (double.IsNaN(value)) value = 0;
            double rounded = Math.Round(value, MidpointRounding.AwayFromZero);
            if (rounded <= min) return min;
            if (rounded >= max) return max;
            return SaturatingCast.ToInt(rounded);
        }

        /// <summary>The same rule over <c>long</c> bounds, the result kept in <c>double</c>: a value of up to 38 bits.</summary>
        internal static double RoundAndClamp(double value, long min, long max)
        {
            if (double.IsNaN(value)) value = 0;
            double rounded = Math.Round(value, MidpointRounding.AwayFromZero);
            if (rounded <= min) return min;
            if (rounded >= max) return max;
            return rounded;
        }

        /// <summary>The smallest sample of a component: 0 unsigned, −2^(precision − 1) signed.</summary>
        internal static long MinSample(int precision, bool isSigned)
        {
            RequirePrecision(precision);
            return isSigned ? -(1L << (precision - 1)) : 0;
        }

        /// <summary>The largest sample of a component: 2^precision − 1 unsigned, 2^(precision − 1) − 1 signed.</summary>
        internal static long MaxSample(int precision, bool isSigned)
        {
            RequirePrecision(precision);
            return isSigned ? (1L << (precision - 1)) - 1 : (1L << precision) - 1;
        }

        /// <summary>
        /// The inverse DC level shift (T.800 G-2): adds 2^(precision − 1) to every sample of an
        /// <b>unsigned</b> component, then clips to the component's range (G.1.2's note). A signed
        /// component is only clipped.
        /// </summary>
        internal static void LevelShiftAndClamp(double[] samples, int precision, bool isSigned) =>
            ShiftAndClamp(samples, precision, isSigned, isSigned ? 0 : 1L << (precision - 1));

        /// <summary>Clips every sample to the component's range, with no level shift — what follows a Part 2 transformation, whose offsets already placed the values.</summary>
        internal static void Clamp(double[] samples, int precision, bool isSigned) =>
            ShiftAndClamp(samples, precision, isSigned, 0);

        /// <summary>The one loop: every (rounded) sample plus <paramref name="shift"/>, clipped to the component's range.</summary>
        private static void ShiftAndClamp(double[] samples, int precision, bool isSigned, long shift)
        {
            long min = MinSample(precision, isSigned), max = MaxSample(precision, isSigned);
            for (int i = 0; i < samples.Length; i++)
            {
                double v = samples[i] + shift;
                samples[i] = v < min ? min : v > max ? max : v;
            }
        }

        /// <summary>The level shift and clip on integer samples: the <c>double</c> one over a copy, for callers that hold integers.</summary>
        internal static void LevelShiftAndClamp(int[] samples, int precision, bool isSigned)
        {
            double[] d = ToDoubles(samples);
            LevelShiftAndClamp(d, precision, isSigned);
            Store(d, samples);
        }

        /// <summary>The clip on integer samples, likewise.</summary>
        internal static void Clamp(int[] samples, int precision, bool isSigned)
        {
            double[] d = ToDoubles(samples);
            Clamp(d, precision, isSigned);
            Store(d, samples);
        }

        internal static void RequirePrecision(int precision)
        {
            if (precision < 1 || precision > MaxSamplePrecision)
                throw new ArgumentOutOfRangeException(nameof(precision), precision, $"a sample plane holds 1 to {MaxSamplePrecision} bits");
        }

        private static void RequireSameLength(int a, int b, int c)
        {
            if (a != b || a != c)
                throw new ArgumentException($"the three components have {a}, {b} and {c} samples; the transformation needs the same count (T.800 G.2)");
        }

        // The one big-endian reader is JpxHeader's; these are its names in this file.
        internal static int U16(byte[] d, long i) => JpxHeader.ReadU16(d, i);

        internal static long U32(byte[] d, long i) => JpxHeader.ReadU32(d, i);
    }

    /// <summary>The array types an MCT marker segment defines (T.801 Table A.33, <c>Imct</c> bits 8–9).</summary>
    internal enum JpxMctArrayType
    {
        Dependency = 0,
        Decorrelation = 1,
        Offset = 2,
    }

    /// <summary>One array of an MCT marker segment, or series (T.801 §A.3.7): its index, type and elements.</summary>
    internal sealed class JpxMctArray
    {
        internal JpxMctArray(int index, JpxMctArrayType type, double[] elements)
        {
            Index = index;
            Type = type;
            Elements = elements;
        }

        /// <summary><c>Imct</c> bits 0–7, 1 to 255.</summary>
        internal int Index { get; }

        internal JpxMctArrayType Type { get; }

        /// <summary>In the marker segment's order; 16- and 32-bit integers, and 32- and 64-bit floats, all as doubles.</summary>
        internal double[] Elements { get; }

        /// <summary>The key a collection's <c>Tmcc</c> looks an array up by: index and type, the ten low-order bits of <c>Imct</c>.</summary>
        internal static int Key(int index, JpxMctArrayType type) => index | ((int)type << 8);
    }

    /// <summary>One component collection of an MCC marker segment (T.801 §A.3.8).</summary>
    internal sealed class JpxComponentCollection
    {
        internal const int DependencyKind = 0, DecorrelationKind = 1, WaveletKind = 3;

        /// <summary><c>Xmcc</c>'s low two bits: 0 array-based dependency, 1 array-based decorrelation, 3 wavelet-based (Table A.35).</summary>
        internal int Kind { get; set; }

        /// <summary><c>Cmcc</c>: the intermediate (codestream) component each input is.</summary>
        internal int[] Inputs { get; set; }

        /// <summary><c>Wmcc</c>: the intermediate component each output becomes.</summary>
        internal int[] Outputs { get; set; }

        /// <summary><c>Tmcc</c> bits 0–7 for an array-based collection: 0 for the identity (J.3.1.1.1).</summary>
        internal int TransformArrayIndex { get; set; }

        /// <summary><c>Tmcc</c> bits 8–15 for an array-based collection: 0 for no offsets.</summary>
        internal int OffsetArrayIndex { get; set; }

        /// <summary><c>Tmcc</c> bit 16 for an array-based collection (Table A.38).</summary>
        internal bool Reversible { get; set; }
    }

    /// <summary>
    /// The Part 2 segments of one header (main, or a tile's first tile-part) as T.801 §A.3.6 to
    /// §A.3.10 lay them out: MCT arrays keyed by index and type (a series with the same <c>Imct</c>
    /// concatenated in <c>Zmct</c> order), MCC collections keyed by <c>Imcc</c> (likewise), the MCO
    /// order, the NLT segments and the CBD depths.
    /// </summary>
    internal sealed class JpxPart2Header
    {
        // The marker codes are the parser's: it kept the segments, this reads them back.
        private const int MCT = JpxCodestreamParser.MCT, MCC = JpxCodestreamParser.MCC, NLT = JpxCodestreamParser.NLT,
            MCO = JpxCodestreamParser.MCO, CBD = JpxCodestreamParser.CBD;

        internal Dictionary<int, JpxMctArray> Arrays { get; } = new Dictionary<int, JpxMctArray>();

        /// <summary>Each MCC segment's collections, by <c>Imcc</c>.</summary>
        internal Dictionary<int, JpxComponentCollection[]> Collections { get; } = new Dictionary<int, JpxComponentCollection[]>();

        /// <summary><c>Imco</c> per stage; null when the header has no MCO, empty for <c>Nmco</c> 0.</summary>
        internal int[] TransformOrder { get; private set; }

        internal List<JpxNonLinearity> NonLinearities { get; } = new List<JpxNonLinearity>();

        /// <summary>CBD: the reconstructed image components' depths; null when the header has none.</summary>
        internal JpxComponentSize[] ReconstructedDepths { get; private set; }

        private sealed class SeriesPart
        {
            internal int Z, Y;
            internal bool HasY;
            internal long Start, End;
        }

        /// <summary>
        /// The main header's Part 2 segments, read once per codestream and kept on it: every tile's
        /// transformation and non-linearity resolve from the same reading, so a codestream of many
        /// tiles copies its MCT series once, not once per tile per stage.
        /// </summary>
        internal static bool TryMain(JpxCodestream cs, out JpxPart2Header main, out string reason)
        {
            reason = null;
            main = cs.MainPart2Header;
            if (main != null) return true;
            if (!TryRead(cs.Data, cs.Main.Part2Segments, main: true, out main, out reason)) return false;
            cs.MainPart2Header = main;
            return true;
        }

        /// <summary>
        /// <see cref="TryMain"/>, and tile <paramref name="tile"/>'s own Part 2 segments read once and
        /// kept on the tile — null when it has none.
        /// </summary>
        internal static bool TryFor(JpxCodestream cs, int tile, out JpxPart2Header main, out JpxPart2Header tileHeader, out string reason)
        {
            tileHeader = null;
            if (!TryMain(cs, out main, out reason)) return false;
            if (!cs.Tiles.TryGetValue(tile, out JpxTile t)) return true;
            if (!t.Part2HeaderRead)
            {
                JpxPart2Header read = null;
                if (t.Settings.Part2Segments.Count > 0 && !TryRead(cs.Data, t.Settings.Part2Segments, main: false, out read, out reason))
                    return false;
                t.Part2Header = read;
                t.Part2HeaderRead = true;
            }
            tileHeader = t.Part2Header;
            return true;
        }

        internal static bool TryRead(byte[] data, List<KeyValuePair<int, JpxRange>> segments, bool main,
            out JpxPart2Header header, out string reason)
        {
            header = new JpxPart2Header();
            reason = null;
            var mctSeries = new Dictionary<int, List<SeriesPart>>();
            var mccSeries = new Dictionary<int, List<SeriesPart>>();
            var mctOrder = new List<int>();
            var mccOrder = new List<int>();

            foreach (KeyValuePair<int, JpxRange> seg in segments)
            {
                long s = seg.Value.Start, e = seg.Value.End;
                switch (seg.Key)
                {
                    case MCT:
                    {
                        if (e - s < 4) return Refuse("an MCT marker segment is shorter than Zmct and Imct (T.801 Table A.32)", out reason);
                        var part = new SeriesPart { Z = JpxComponentTransforms.U16(data, s), Start = s + 4, End = e };
                        int imct = JpxComponentTransforms.U16(data, s + 2);
                        if (part.Z == 0)
                        {
                            if (e - s < 6) return Refuse("the first MCT marker segment of a series is shorter than Zmct, Imct and Ymct (T.801 Table A.32)", out reason);
                            part.HasY = true;
                            part.Y = JpxComponentTransforms.U16(data, s + 4);
                            part.Start = s + 6;
                        }
                        if (!mctSeries.TryGetValue(imct, out List<SeriesPart> parts)) { mctSeries[imct] = parts = new List<SeriesPart>(); mctOrder.Add(imct); }
                        parts.Add(part);
                        break;
                    }
                    case MCC:
                    {
                        if (e - s < 3) return Refuse("an MCC marker segment is shorter than Zmcc and Imcc (T.801 Table A.34)", out reason);
                        var part = new SeriesPart { Z = JpxComponentTransforms.U16(data, s), Start = s + 3, End = e };
                        int imcc = data[s + 2];
                        if (part.Z == 0)
                        {
                            if (e - s < 7) return Refuse("the first MCC marker segment of a series is shorter than Zmcc, Imcc, Ymcc and Qmcc (T.801 Table A.34)", out reason);
                            part.HasY = true;
                            part.Y = JpxComponentTransforms.U16(data, s + 3);
                            part.Start = s + 5;   // Qmcc stays at the head of the concatenated stream
                        }
                        if (!mccSeries.TryGetValue(imcc, out List<SeriesPart> parts)) { mccSeries[imcc] = parts = new List<SeriesPart>(); mccOrder.Add(imcc); }
                        parts.Add(part);
                        break;
                    }
                    case MCO:
                    {
                        if (header.TransformOrder != null) return Refuse("a header has a second MCO marker segment (T.801 §A.3.9)", out reason);
                        if (e - s < 1) return Refuse("an MCO marker segment has no Nmco (T.801 Table A.40)", out reason);
                        int nmco = data[s];
                        if (e - s != 1 + nmco) return Refuse($"an MCO marker segment declares {nmco} stages but holds {e - s - 1} (T.801 A-6)", out reason);
                        var order = new int[nmco];
                        for (int i = 0; i < nmco; i++) order[i] = data[s + 1 + i];
                        header.TransformOrder = order;
                        break;
                    }
                    case NLT:
                    {
                        if (!JpxNonLinearity.TryParse(data, seg.Value, out JpxNonLinearity nlt, out reason)) return false;
                        foreach (JpxNonLinearity other in header.NonLinearities)
                            if (other.Component == nlt.Component)
                                return Refuse($"a header has two NLT marker segments for {(nlt.Component == JpxNonLinearity.AllComponents ? "all components" : "component " + nlt.Component)} (T.801 §A.3.10)", out reason);
                        header.NonLinearities.Add(nlt);
                        break;
                    }
                    case CBD:
                    {
                        if (!main) return Refuse("CBD appears in a tile-part header; it belongs in the main header (T.801 §A.3.6)", out reason);
                        if (header.ReconstructedDepths != null) return Refuse("the main header has a second CBD marker segment (T.801 §A.3.6)", out reason);
                        if (e - s < 3) return Refuse("a CBD marker segment is shorter than Ncbd and one depth (T.801 Table A.29)", out reason);
                        int ncbd = JpxComponentTransforms.U16(data, s);
                        bool one = (ncbd & 0x8000) != 0;
                        int count = ncbd & 0x7FFF;
                        if (count < 1 || count > JpxImageSize.MaxComponents)
                            return Refuse($"CBD declares {count} reconstructed components; T.801 Table A.30 allows 1 to {JpxImageSize.MaxComponents}", out reason);
                        if (e - s != 2 + (one ? 1 : count))
                            return Refuse($"CBD declares {count} reconstructed components{(one ? " with one depth" : "")} but holds {e - s - 2} depth bytes (T.801 A-5)", out reason);
                        var depths = new JpxComponentSize[count];
                        for (int i = 0; i < count; i++)
                        {
                            int bd = data[s + 2 + (one ? 0 : i)];
                            int precision = (bd & 0x7F) + 1;
                            if (precision > 38) return Refuse($"CBD gives reconstructed component {i} {precision}-bit samples; T.801 Table A.31 allows 1 to 38", out reason);
                            depths[i] = new JpxComponentSize(precision, (bd & 0x80) != 0, 1, 1);
                        }
                        header.ReconstructedDepths = depths;
                        break;
                    }
                }
            }

            foreach (int imct in mctOrder)
            {
                if (!TryJoinSeries(data, mctSeries[imct], "MCT", "Zmct", "Ymct", out byte[] payload, out reason)) return false;
                if (!TryDecodeArray(imct, payload, header, out reason)) return false;
            }
            foreach (int imcc in mccOrder)
            {
                if (!TryJoinSeries(data, mccSeries[imcc], "MCC", "Zmcc", "Ymcc", out byte[] payload, out reason)) return false;
                if (!TryDecodeCollections(imcc, payload, header, out reason)) return false;
            }
            return true;
        }

        /// <summary>A series' payloads in Z order, Z running 0 to Y exactly once each (T.801 §J.2).</summary>
        private static bool TryJoinSeries(byte[] data, List<SeriesPart> parts, string name, string z, string y, out byte[] payload, out string reason)
        {
            payload = null;
            reason = null;
            parts.Sort((a, b) => a.Z.CompareTo(b.Z));
            if (!parts[0].HasY) return Refuse($"an {name} series has no marker segment with {z} 0 (T.801 §A.3.7, §A.3.8)", out reason);
            int last = parts[0].Y;
            if (parts.Count != last + 1) return Refuse($"an {name} series declares {y} {last} but has {parts.Count} marker segments (T.801 §A.3.7, §A.3.8)", out reason);
            long total = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                if (parts[i].Z != i) return Refuse($"an {name} series has {z} values out of sequence (T.801 §A.3.7, §A.3.8)", out reason);
                total += parts[i].End - parts[i].Start;
            }
            payload = new byte[total];
            long at = 0;
            foreach (SeriesPart p in parts)
            {
                Array.Copy(data, p.Start, payload, at, p.End - p.Start);
                at += p.End - p.Start;
            }
            return true;
        }

        /// <summary>T.801 Table A.33: index in bits 0–7, type in bits 8–9, element type in bits 10–11; the elements big-endian.</summary>
        private static bool TryDecodeArray(int imct, byte[] payload, JpxPart2Header header, out string reason)
        {
            reason = null;
            int index = imct & 0xFF;
            int type = (imct >> 8) & 0x03;
            int elementType = (imct >> 10) & 0x03;
            if (index == 0) return Refuse("an MCT marker segment has array index 0; T.801 Table A.33 allows 1 to 255", out reason);
            if (type == 3) return Refuse($"an MCT marker segment (index {index}) has the reserved array type 3 (T.801 Table A.33)", out reason);
            int size = elementType == 0 ? 2 : elementType == 1 ? 4 : elementType == 2 ? 4 : 8;
            if (payload.Length == 0 || payload.Length % size != 0)
                return Refuse($"an MCT marker segment (index {index}) holds {payload.Length} bytes, not a whole number of {size}-byte elements (T.801 Table A.33)", out reason);

            var elements = new double[payload.Length / size];
            for (int i = 0; i < elements.Length; i++)
            {
                int at = i * size;
                switch (elementType)
                {
                    case 0:
                        elements[i] = (short)JpxComponentTransforms.U16(payload, at);
                        break;
                    case 1:
                        elements[i] = (int)JpxComponentTransforms.U32(payload, at);
                        break;
                    case 2:
                        elements[i] = BitConverter.ToSingle(BitConverter.GetBytes((int)JpxComponentTransforms.U32(payload, at)), 0);
                        break;
                    default:
                        long bits = (JpxComponentTransforms.U32(payload, at) << 32) | JpxComponentTransforms.U32(payload, at + 4);
                        elements[i] = BitConverter.Int64BitsToDouble(bits);
                        break;
                }
            }
            var array = new JpxMctArray(index, (JpxMctArrayType)type, elements);
            int key = JpxMctArray.Key(index, array.Type);
            if (header.Arrays.ContainsKey(key))
                return Refuse($"a header defines the {array.Type} array {index} twice (T.801 §A.3.7)", out reason);
            header.Arrays[key] = array;
            return true;
        }

        /// <summary>T.801 Table A.34 after Ymcc: Qmcc, then per collection Xmcc, Nmcc and the inputs, Mmcc and the outputs, Tmcc (and Omcc for a wavelet).</summary>
        private static bool TryDecodeCollections(int imcc, byte[] p, JpxPart2Header header, out string reason)
        {
            reason = null;
            int qmcc = JpxComponentTransforms.U16(p, 0);
            var collections = new JpxComponentCollection[qmcc];
            long at = 2;
            for (int i = 0; i < qmcc; i++)
            {
                var c = new JpxComponentCollection();
                if (at + 3 > p.Length) return Truncated(imcc, out reason);
                c.Kind = p[at] & 0x03;
                at++;
                if (!TryReadIndices(p, ref at, out int[] inputs, out reason, imcc)) return false;
                c.Inputs = inputs;
                if (!TryReadIndices(p, ref at, out int[] outputs, out reason, imcc)) return false;
                c.Outputs = outputs;
                if (c.Kind == JpxComponentCollection.WaveletKind)
                {
                    if (at + 8 > p.Length) return Truncated(imcc, out reason);
                    at += 8;   // Tmcc (32 bits) and Omcc: outside the baseline, refused at resolution by kind
                }
                else
                {
                    if (at + 3 > p.Length) return Truncated(imcc, out reason);
                    int tmcc = (p[at] << 16) | (p[at + 1] << 8) | p[at + 2];
                    at += 3;
                    c.TransformArrayIndex = tmcc & 0xFF;
                    c.OffsetArrayIndex = (tmcc >> 8) & 0xFF;
                    c.Reversible = (tmcc & 0x10000) != 0;
                }
                collections[i] = c;
            }
            if (at != p.Length) return Refuse($"MCC {imcc} has {p.Length - at} bytes after its {qmcc} collections (T.801 Table A.34)", out reason);
            if (header.Collections.ContainsKey(imcc)) return Refuse($"a header has two MCC marker segments with Imcc {imcc} (T.801 §A.3.8)", out reason);
            header.Collections[imcc] = collections;
            return true;
        }

        /// <summary>Nmcc or Mmcc (bit 15: 16-bit indices; bits 0–14: the count, 1 to 16 384), then the indices.</summary>
        private static bool TryReadIndices(byte[] p, ref long at, out int[] indices, out string reason, int imcc)
        {
            indices = null;
            reason = null;
            if (at + 2 > p.Length) return Truncated(imcc, out reason);
            int n = JpxComponentTransforms.U16(p, at);
            at += 2;
            bool wide = (n & 0x8000) != 0;
            int count = n & 0x7FFF;
            if (count < 1 || count > JpxImageSize.MaxComponents)
                return Refuse($"MCC {imcc} declares a collection of {count} components; T.801 Tables A.36 and A.37 allow 1 to {JpxImageSize.MaxComponents}", out reason);
            int width = wide ? 2 : 1;
            if (at + (long)count * width > p.Length) return Truncated(imcc, out reason);
            indices = new int[count];
            for (int i = 0; i < count; i++)
            {
                indices[i] = wide ? JpxComponentTransforms.U16(p, at) : p[at];
                at += width;
            }
            return true;
        }

        private static bool Truncated(int imcc, out string reason) =>
            Refuse($"MCC {imcc} ends inside a component collection (T.801 Table A.34)", out reason);

        internal static bool Refuse(string why, out string reason)
        {
            reason = why;
            return false;
        }
    }

    /// <summary>
    /// The Part 2 irreversible decorrelation transformation (T.801 J.3.1.1.1) a tile signals through
    /// MCO → MCC → MCT, resolved through the header precedence of §A.3.7 to §A.3.9 and checked
    /// against the JPX baseline (§M.9.2.3: <c>Qmcc</c> 1, array-based decorrelation, irreversible,
    /// <c>Nmco</c> 1). Equation J-2: W<sub>i</sub> = Σ<sub>j</sub> t<sub>ij</sub> C<sub>j</sub> + o<sub>i</sub>.
    /// </summary>
    /// <remarks>
    /// The SIZ then describes the codestream components; the CBD describes the reconstructed ones
    /// (J.2.1, K.1.2), which <see cref="ReconstructedComponents"/> lists with the inputs' separation.
    /// No Annex G level shift follows: the offset array is the offset (Annex J's opening paragraph).
    /// An output the collection does not name is a null component, zero (J.2.3.3).
    /// </remarks>
    internal sealed class JpxMultipleComponentTransform
    {
        /// <summary>T.801 Table A.2's multiple component transformation bit of <c>Rsiz</c>.</summary>
        private const int CapabilityBit = 1 << 8;

        /// <summary>The deviation for MCT segments a codestream carries without declaring the capability (T.801 §A.3.9) — reported to the caller's list, which the decode's spine merges once.</summary>
        internal const string CapabilityNote = "MCO, MCC and MCT segments are present although Rsiz does not declare the multiple component transformation capability (T.801 §A.3.9); applied";

        private JpxMultipleComponentTransform(int[] inputs, int[] outputs, double[] matrix, double[] offsets, JpxComponentSize[] reconstructed)
        {
            Inputs = inputs;
            Outputs = outputs;
            Matrix = matrix;
            Offsets = offsets;
            ReconstructedComponents = reconstructed;
        }

        /// <summary>The codestream component each input column C<sub>j</sub> is (<c>Cmcc</c>).</summary>
        internal int[] Inputs { get; }

        /// <summary>The reconstructed component each output row W<sub>i</sub> becomes (<c>Wmcc</c>).</summary>
        internal int[] Outputs { get; }

        /// <summary>
        /// t<sub>ij</sub>, M rows of N, row-major (J.3.1.1.1's storage order); <b>null for the identity</b>
        /// (<c>Tmcc</c> array index 0), which <see cref="Apply"/> applies as a copy — an M by N
        /// identity of 16 384 components each would be 2 GB of doubles for a 60 KB file.
        /// </summary>
        internal double[] Matrix { get; }

        /// <summary>o<sub>i</sub>, one per output row; zeros without an offset array.</summary>
        internal double[] Offsets { get; }

        /// <summary>The CBD's depths, one per reconstructed component, carrying the inputs' separation on the grid.</summary>
        internal JpxComponentSize[] ReconstructedComponents { get; }

        /// <summary>
        /// The transformation tile <paramref name="tile"/> signals: null, and true, when it signals none
        /// (no MCO in effect, or <c>Nmco</c> 0 — J.2.1); false with the reason for a malformed set or one
        /// outside the JPX baseline.
        /// </summary>
        internal static bool TryResolve(JpxCodestream cs, int tile, out JpxMultipleComponentTransform transform, out string reason) =>
            TryResolve(cs, tile, null, out transform, out reason);

        /// <summary>
        /// As <see cref="TryResolve(JpxCodestream, int, out JpxMultipleComponentTransform, out string)"/>,
        /// with <paramref name="deviations"/> receiving <see cref="CapabilityNote"/> when the segments
        /// are present without the capability bit — the tile's list, so the note reaches the
        /// decode's result; the codestream's own list was already merged when a tile is resolved.
        /// </summary>
        internal static bool TryResolve(JpxCodestream cs, int tile, List<string> deviations, out JpxMultipleComponentTransform transform, out string reason)
        {
            transform = null;
            if (!JpxPart2Header.TryFor(cs, tile, out JpxPart2Header main, out JpxPart2Header tileHeader, out reason)) return false;

            int[] order = tileHeader?.TransformOrder ?? main.TransformOrder;
            if (order == null || order.Length == 0) return true;
            if (order.Length > 1)
                return JpxPart2Header.Refuse($"the MCO declares {order.Length} transformation stages; the JPX baseline allows one (T.801 §M.9.2.3)", out reason);
            if ((cs.Part2Capabilities & CapabilityBit) == 0 && deviations != null && !deviations.Contains(CapabilityNote))
                deviations.Add(CapabilityNote);

            int imcc = order[0];
            JpxComponentCollection[] collections = null;
            if (tileHeader == null || !tileHeader.Collections.TryGetValue(imcc, out collections))
                main.Collections.TryGetValue(imcc, out collections);
            if (collections == null)
                return JpxPart2Header.Refuse($"the MCO names MCC {imcc}, which no header defines (T.801 §A.3.9)", out reason);
            if (collections.Length != 1)
                return JpxPart2Header.Refuse($"MCC {imcc} holds {collections.Length} component collections; the JPX baseline allows one (Qmcc 1, T.801 §M.9.2.3)", out reason);
            JpxComponentCollection c = collections[0];
            if (c.Kind == JpxComponentCollection.DependencyKind)
                return JpxPart2Header.Refuse($"MCC {imcc} uses an array-based dependency transformation; the JPX baseline allows only the array-based decorrelation one (T.801 §M.9.2.3)", out reason);
            if (c.Kind == JpxComponentCollection.WaveletKind)
                return JpxPart2Header.Refuse($"MCC {imcc} uses a wavelet-based transformation; the JPX baseline allows only the array-based decorrelation one (T.801 §M.9.2.3)", out reason);
            if (c.Kind != JpxComponentCollection.DecorrelationKind)
                return JpxPart2Header.Refuse($"MCC {imcc} uses the reserved transformation kind {c.Kind} (T.801 Table A.35)", out reason);
            if (c.Reversible)
                return JpxPart2Header.Refuse($"MCC {imcc} uses a reversible decorrelation transformation; the JPX baseline allows only an irreversible one (T.801 §M.9.2.3)", out reason);

            JpxComponentSize[] depths = main.ReconstructedDepths;
            if (depths == null)
                return JpxPart2Header.Refuse("a multiple component transformation is signalled without a CBD marker segment, which it requires (T.801 §J.2.1)", out reason);

            int csiz = cs.Size.Components.Length;
            var touched = new bool[csiz];
            JpxComponentSize first = null;
            foreach (int input in c.Inputs)
            {
                if (input >= csiz)
                    return JpxPart2Header.Refuse($"MCC {imcc} reads component {input} of a {csiz}-component codestream (T.801 §J.2.3.1)", out reason);
                JpxComponentSize size = cs.Size.Components[input];
                if (first == null) first = size;
                else if (size.XRsiz != first.XRsiz || size.YRsiz != first.YRsiz)
                    return JpxPart2Header.Refuse($"MCC {imcc} combines component {input}, sub-sampled {size.XRsiz}x{size.YRsiz}, with component {c.Inputs[0]}, sub-sampled {first.XRsiz}x{first.YRsiz}; a collection's inputs must share their dimensions (T.801 §J.2.3)", out reason);
                touched[input] = true;
            }
            for (int i = 0; i < csiz; i++)
                if (!touched[i])
                    return JpxPart2Header.Refuse($"MCC {imcc} leaves component {i} out of its input list; every component must appear in one (T.801 §J.2.3.1)", out reason);

            var assigned = new bool[depths.Length];
            foreach (int output in c.Outputs)
            {
                if (output >= depths.Length)
                    return JpxPart2Header.Refuse($"MCC {imcc} writes reconstructed component {output} of the {depths.Length} the CBD declares (T.801 §J.2.3.3)", out reason);
                if (assigned[output])
                    return JpxPart2Header.Refuse($"MCC {imcc} writes reconstructed component {output} twice (T.801 §A.3.8)", out reason);
                assigned[output] = true;
            }

            int n = c.Inputs.Length, m = c.Outputs.Length;
            double[] matrix;
            if (c.TransformArrayIndex == 0)
            {
                matrix = null;   // the identity, applied without being materialised
            }
            else
            {
                if (!TryFindArray(tileHeader, main, c.TransformArrayIndex, JpxMctArrayType.Decorrelation, out JpxMctArray array))
                    return JpxPart2Header.Refuse($"MCC {imcc} names decorrelation array {c.TransformArrayIndex}, which no MCT marker segment defines (T.801 §J.3.1)", out reason);
                if (array.Elements.Length != m * n)
                    return JpxPart2Header.Refuse($"decorrelation array {c.TransformArrayIndex} holds {array.Elements.Length} elements; the collection's {m} outputs of {n} inputs need {m * n} (T.801 §J.3.1.1.1)", out reason);
                matrix = array.Elements;
            }

            var offsets = new double[m];
            if (c.OffsetArrayIndex != 0)
            {
                if (!TryFindArray(tileHeader, main, c.OffsetArrayIndex, JpxMctArrayType.Offset, out JpxMctArray array))
                    return JpxPart2Header.Refuse($"MCC {imcc} names offset array {c.OffsetArrayIndex}, which no MCT marker segment defines (T.801 §J.3.1)", out reason);
                if (array.Elements.Length != m)
                    return JpxPart2Header.Refuse($"offset array {c.OffsetArrayIndex} holds {array.Elements.Length} elements; the collection's {m} outputs need {m} (T.801 §J.3.1.1.1)", out reason);
                offsets = array.Elements;
            }

            var reconstructed = new JpxComponentSize[depths.Length];
            for (int i = 0; i < depths.Length; i++)
                reconstructed[i] = new JpxComponentSize(depths[i].Precision, depths[i].IsSigned, first.XRsiz, first.YRsiz);
            transform = new JpxMultipleComponentTransform(c.Inputs, c.Outputs, matrix, offsets, reconstructed);
            return true;
        }

        /// <summary>An array by index and type: the tile's header first, then the main header (T.801 §A.3.7, §A.3.8).</summary>
        private static bool TryFindArray(JpxPart2Header tileHeader, JpxPart2Header main, int index, JpxMctArrayType type, out JpxMctArray array)
        {
            int key = JpxMctArray.Key(index, type);
            array = null;
            return (tileHeader != null && tileHeader.Arrays.TryGetValue(key, out array)) || main.Arrays.TryGetValue(key, out array);
        }

        /// <summary>
        /// J-2 over every sample: reads the <see cref="Inputs"/> of <paramref name="codestreamComponents"/>
        /// (indexed by codestream component, all of one length) and writes the <see cref="Outputs"/> of
        /// <paramref name="reconstructed"/> (indexed by reconstructed component, each of that length,
        /// supplied by the caller — a component no output names is left as given: zero is J.2.3.3's
        /// null component). <paramref name="budget"/>, when given, is charged one unit per
        /// <see cref="JpxDecoder.SamplesPerUnit"/> multiply-adds — an explicit M by N array over a
        /// tile is M × N passes over it — and one per output copy for the identity.
        /// </summary>
        internal void Apply(double[][] codestreamComponents, double[][] reconstructed, WorkBudget budget = null)
        {
            int n = Inputs.Length, m = Outputs.Length;
            var inputs = new double[n][];
            int length = codestreamComponents[Inputs[0]].Length;
            for (int j = 0; j < n; j++)
            {
                inputs[j] = codestreamComponents[Inputs[j]];
                if (inputs[j].Length != length)
                    throw new ArgumentException($"input component {Inputs[j]} has {inputs[j].Length} samples, component {Inputs[0]} {length}; a collection's inputs share their dimensions (T.801 §J.2.3)", nameof(codestreamComponents));
            }
            var outputs = new double[m][];
            for (int i = 0; i < m; i++)
            {
                outputs[i] = reconstructed[Outputs[i]];
                if (outputs[i].Length != length)
                    throw new ArgumentException($"reconstructed component {Outputs[i]} has {outputs[i].Length} samples, the inputs {length}", nameof(reconstructed));
            }

            budget?.Tick(1 + (long)length * m * (Matrix == null ? 1 : n) / JpxDecoder.SamplesPerUnit);
            if (Matrix == null)
            {
                // The identity: W_i = C_i + o_i for the inputs there are; a row past them is its offset alone.
                for (int i = 0; i < m; i++)
                {
                    double offset = Offsets[i];
                    double[] output = outputs[i];
                    if (i < n)
                    {
                        double[] input = inputs[i];
                        for (int s = 0; s < length; s++) output[s] = offset + input[s];
                    }
                    else
                    {
                        for (int s = 0; s < length; s++) output[s] = offset;
                    }
                }
                return;
            }

            for (int s = 0; s < length; s++)
                for (int i = 0; i < m; i++)
                {
                    double acc = Offsets[i];
                    int row = i * n;
                    for (int j = 0; j < n; j++) acc += Matrix[row + j] * inputs[j][s];
                    outputs[i][s] = acc;
                }
        }
    }

    /// <summary>
    /// A non-linearity point transformation (T.801 Annex K) an NLT marker segment describes
    /// (§A.3.10): gamma-style (K-2, on values normalised by K-3), LUT-style (K-6 to K-9), or the
    /// binary-complement-to-sign-magnitude conversion (K.2.3). Applied to a reconstructed component
    /// after the level shift — Figure K.1 — and changing its depth to <see cref="OutputPrecision"/>.
    /// </summary>
    internal sealed class JpxNonLinearity
    {
        internal const int AllComponents = 65535;
        internal const int None = 0, Gamma = 1, Lookup = 2, SignMagnitude = 3;

        /// <summary><c>Cnlt</c>: a component index, or <see cref="AllComponents"/> for the header's default.</summary>
        internal int Component { get; private set; }

        /// <summary><c>Tnlt</c> (Table A.44).</summary>
        internal int Type { get; private set; }

        /// <summary><c>BDnlt</c>: the decoded component's depth after the transformation (Table A.43).</summary>
        internal int OutputPrecision { get; private set; }

        internal bool OutputSigned { get; private set; }

        /// <summary>Gamma parameters (Table A.45), each an 8-bit integer with a 16-bit fraction.</summary>
        internal double E { get; private set; }
        internal double S { get; private set; }
        internal double T { get; private set; }
        internal double A { get; private set; }
        internal double B { get; private set; }

        /// <summary>The LUT's input range, normalised (Table A.46).</summary>
        internal double Dmin { get; private set; }
        internal double Dmax { get; private set; }

        /// <summary>t<sub>k</sub> = T<sub>k</sub> / (2^PTval − 1): the LUT's outputs, normalised, evenly spaced over [Dmin, Dmax].</summary>
        internal double[] Table { get; private set; }

        /// <summary>Reads one NLT marker segment's parameters (T.801 Table A.41 to A.46).</summary>
        internal static bool TryParse(byte[] data, JpxRange seg, out JpxNonLinearity nlt, out string reason)
        {
            nlt = null;
            reason = null;
            long s = seg.Start, e = seg.End;
            if (e - s < 4) return JpxPart2Header.Refuse("an NLT marker segment is shorter than Cnlt, BDnlt and Tnlt (T.801 Table A.41)", out reason);
            var n = new JpxNonLinearity
            {
                Component = JpxComponentTransforms.U16(data, s),
                OutputPrecision = (data[s + 2] & 0x7F) + 1,
                OutputSigned = (data[s + 2] & 0x80) != 0,
                Type = data[s + 3],
            };
            if (n.Component != AllComponents && n.Component >= JpxImageSize.MaxComponents)
                return JpxPart2Header.Refuse($"an NLT marker segment names component {n.Component}; T.801 Table A.42 allows 0 to {JpxImageSize.MaxComponents - 1} or {AllComponents}", out reason);
            if (n.OutputPrecision > 38)
                return JpxPart2Header.Refuse($"an NLT marker segment gives {n.OutputPrecision}-bit decoded samples; T.801 Table A.43 allows 1 to 38", out reason);
            long at = s + 4;
            switch (n.Type)
            {
                case None:
                case SignMagnitude:
                    break;
                case Gamma:
                    if (e - at != 15) return JpxPart2Header.Refuse($"a gamma-style NLT marker segment holds {e - at} parameter bytes, not the 15 of T.801 Table A.45", out reason);
                    n.E = Fixed8_16(data, at);
                    n.S = Fixed8_16(data, at + 3);
                    n.T = Fixed8_16(data, at + 6);
                    n.A = Fixed8_16(data, at + 9);
                    n.B = Fixed8_16(data, at + 12);
                    if (n.E == 0 || n.S == 0 || n.A == 0)
                        return JpxPart2Header.Refuse("a gamma-style NLT marker segment has a zero E, S or A, which leaves K-2 undefined (T.801 Table A.45)", out reason);
                    break;
                case Lookup:
                {
                    if (e - at < 11) return JpxPart2Header.Refuse("a LUT-style NLT marker segment is shorter than Npoints, Dmin, Dmax and PTval (T.801 Table A.46)", out reason);
                    int points = JpxComponentTransforms.U16(data, at) + 1;   // the field holds the count less one
                    n.Dmin = JpxComponentTransforms.U32(data, at + 2) / 4294967295.0;
                    n.Dmax = JpxComponentTransforms.U32(data, at + 6) / 4294967295.0;
                    int ptval = data[at + 10];
                    if (points < 2) return JpxPart2Header.Refuse("a LUT-style NLT marker segment declares fewer than two points (T.801 Table A.46)", out reason);
                    if (n.Dmax <= n.Dmin) return JpxPart2Header.Refuse("a LUT-style NLT marker segment has Dmax at or below Dmin (T.801 K-6)", out reason);
                    if (ptval < 1 || ptval > 32) return JpxPart2Header.Refuse($"a LUT-style NLT marker segment has PTval {ptval}; T.801 Table A.46 allows 1 to 32", out reason);
                    int width = ptval <= 8 ? 1 : ptval <= 16 ? 2 : 4;
                    at += 11;
                    if (e - at != (long)points * width)
                        return JpxPart2Header.Refuse($"a LUT-style NLT marker segment declares {points} points of {width} bytes but holds {e - at} bytes (T.801 Table A.46)", out reason);
                    double scale = Math.Pow(2, ptval) - 1;
                    n.Table = new double[points];
                    for (int k = 0; k < points; k++)
                    {
                        long raw = width == 1 ? data[at] : width == 2 ? JpxComponentTransforms.U16(data, at) : JpxComponentTransforms.U32(data, at);
                        n.Table[k] = raw / scale;
                        at += width;
                    }
                    break;
                }
                default:
                    return JpxPart2Header.Refuse($"an NLT marker segment has the reserved type {n.Type} (T.801 Table A.44)", out reason);
            }
            if (n.Type == None || n.Type == SignMagnitude)
            {
                if (e - at != 0) return JpxPart2Header.Refuse($"an NLT marker segment of type {n.Type} holds {e - at} parameter bytes; it takes none (T.801 A-7)", out reason);
            }
            nlt = n;
            return true;
        }

        /// <summary>An 8-bit integer with a 16-bit fraction (T.801 Table A.45).</summary>
        private static double Fixed8_16(byte[] d, long at) => ((d[at] << 16) | (d[at + 1] << 8) | d[at + 2]) / 65536.0;

        /// <summary>
        /// The non-linearity of each of <paramref name="components"/> reconstructed components in
        /// tile <paramref name="tile"/> — tile per component &gt; tile default &gt; main per component
        /// &gt; main default (T.801 §A.3.10) — null where none applies or the one in effect is of type
        /// <see cref="None"/>. False with the reason for a malformed segment.
        /// </summary>
        internal static bool TryResolve(JpxCodestream cs, int tile, int components, out JpxNonLinearity[] perComponent, out string reason)
        {
            perComponent = null;
            if (!JpxPart2Header.TryFor(cs, tile, out JpxPart2Header main, out JpxPart2Header tileHeader, out reason)) return false;

            perComponent = new JpxNonLinearity[components];
            for (int c = 0; c < components; c++)
            {
                JpxNonLinearity n = Find(tileHeader, c) ?? Find(tileHeader, AllComponents) ?? Find(main, c) ?? Find(main, AllComponents);
                perComponent[c] = n == null || n.Type == None ? null : n;
            }
            return true;
        }

        private static JpxNonLinearity Find(JpxPart2Header header, int component)
        {
            if (header == null) return null;
            foreach (JpxNonLinearity n in header.NonLinearities)
                if (n.Component == component) return n;
            return null;
        }

        /// <summary>
        /// Applies the transformation in place to a component's level-shifted samples of
        /// <paramref name="inputPrecision"/> bits; on return they are <see cref="OutputPrecision"/>-bit
        /// samples, clipped to that range.
        /// </summary>
        internal void Apply(int[] samples, int inputPrecision, bool inputSigned)
        {
            var d = new double[samples.Length];
            for (int i = 0; i < d.Length; i++) d[i] = samples[i];
            Apply(d, inputPrecision, inputSigned);
            JpxComponentTransforms.Store(d, samples);
        }

        /// <summary>The transformation on the <c>double</c> planes the decoder holds, integer-valued in and out (see the class remarks of <see cref="JpxComponentTransforms"/>).</summary>
        internal void Apply(double[] samples, int inputPrecision, bool inputSigned)
        {
            JpxComponentTransforms.RequirePrecision(inputPrecision);
            JpxComponentTransforms.RequirePrecision(OutputPrecision);
            long min = JpxComponentTransforms.MinSample(OutputPrecision, OutputSigned);
            long max = JpxComponentTransforms.MaxSample(OutputPrecision, OutputSigned);
            switch (Type)
            {
                case Gamma:
                {
                    // K-3: the value whose normalised form is +1 — the largest magnitude, for a signed component.
                    double fy = inputSigned ? Math.Pow(2, inputPrecision - 1) : Math.Pow(2, inputPrecision) - 1;
                    double fz = OutputSigned ? Math.Pow(2, OutputPrecision - 1) : Math.Pow(2, OutputPrecision) - 1;
                    double inverseE = 1 / E;
                    for (int i = 0; i < samples.Length; i++)
                    {
                        double y = samples[i] / fy;
                        double z = y < -T ? -Math.Pow((-y + B) / A, inverseE)
                            : y <= T ? y / S
                            : Math.Pow((y + B) / A, inverseE);
                        samples[i] = JpxComponentTransforms.RoundAndClamp(z * fz, min, max);
                    }
                    break;
                }
                case Lookup:
                {
                    // K-7: the input shifted and scaled onto [0, 1]; K-6, K-8: linear between the two points around it; K-9: back to samples.
                    double inRange = Math.Pow(2, inputPrecision) - 1;
                    double inOffset = inputSigned ? Math.Pow(2, inputPrecision - 1) : 0;
                    double outRange = Math.Pow(2, OutputPrecision) - 1;
                    double outOffset = OutputSigned ? Math.Pow(2, OutputPrecision - 1) : 0;
                    double delta = (Dmax - Dmin) / (Table.Length - 1);
                    for (int i = 0; i < samples.Length; i++)
                    {
                        double y = (samples[i] + inOffset) / inRange;
                        if (y < Dmin) y = Dmin;
                        else if (y > Dmax) y = Dmax;
                        double position = (y - Dmin) / delta;
                        int k = SaturatingCast.ToInt(Math.Floor(position));
                        if (k > Table.Length - 2) k = Table.Length - 2;
                        double z = Table[k] + (position - k) * (Table[k + 1] - Table[k]);
                        samples[i] = JpxComponentTransforms.RoundAndClamp(z * outRange - outOffset, min, max);
                    }
                    break;
                }
                case SignMagnitude:
                {
                    // K.2.3: Y = Z for Z ≥ 0, else min(−2^(b−1) − Z − 1, −1); the identity for an unsigned component.
                    if (!OutputSigned) break;
                    double low = -(1L << (OutputPrecision - 1));
                    for (int i = 0; i < samples.Length; i++)
                    {
                        double v = samples[i];
                        if (v < 0) samples[i] = Math.Min(low - v - 1, -1);
                    }
                    break;
                }
            }
        }
    }
}
