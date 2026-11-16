// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using SkiaSharp;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// An ICC.1 <c>A2B0</c> device→PCS lookup table in its <c>lut8Type</c> (<c>mft1</c>) or
    /// <c>lut16Type</c> (<c>mft2</c>) form: per-channel input curves, an <i>n</i>-dimensional
    /// colour lookup table, and per-channel output curves.
    ///
    /// <para><b>Why this exists, and what it is not.</b> Every CMYK profile is lut-based —
    /// there is no matrix/TRC form for four inks — so without this the entire CMYK half of ICC
    /// colour management would fall back to <c>PdfColor.CmykToRgb</c>, which is a fixed formula that
    /// knows nothing about the press the profile describes. This evaluates the profile's own
    /// table — the <c>A2B</c> or <c>B2A</c> the rendering intent selects (<c>IccProfile</c> picks the
    /// tag; black-point compensation is <c>IccBlackPoint</c>'s and <c>IccOutputTransform</c>'s). It is
    /// still not a full CMM: there is no gamut mapping beyond what the profile's own tables
    /// encode.</para>
    ///
    /// <para><b>The whole tag is untrusted.</b> Channel counts, grid size and table lengths are
    /// all file-supplied bytes that size reads and allocations. Every one is bounded before use,
    /// and anything that does not check out returns <c>null</c> with a reason rather than
    /// throwing — the caller is a render path that has to keep going.</para>
    /// </summary>
    /// <remarks>
    /// <para><b>The CLUT is read in place.</b> A 4-channel profile on a 33-point grid is
    /// 33⁴ × 3 samples — about 3.5 M entries — and unpacking that to <c>double</c> would cost 28 MB
    /// per profile for no gain, since interpolation touches 2ⁿ of them per colour. The tag's own
    /// bytes are indexed instead. Holding the array is safe: <c>IccColorTransform</c> keys its
    /// parse cache on that same array instance, so the profile and its bytes have one lifetime.
    /// The input and output <i>curves</i> are small and fixed-size, and those are unpacked.</para>
    /// </remarks>
    internal sealed class IccLut
    {
        /// <summary>
        /// Largest device-channel count evaluated. Interpolation visits 2ⁿ corners, so this is an
        /// exponent rather than a length — the same reasoning as
        /// <see cref="PdfLimits.MaxSampledFunctionInterpolationInputs"/>, which bounds the identical
        /// shape in a Type 0 PDF function. ICC.1 allows up to 15 input channels; 8 covers every
        /// device space a PDF can name (CMYK is 4, hexachrome 6) and bounds the corner loop at 256.
        /// </summary>
        internal const int MaxInputChannels = 8;

        /// <summary>
        /// Largest output-channel count. The PCS is three components; the extra headroom costs
        /// nothing and keeps a profile with a padded output table readable.
        /// </summary>
        private const int MaxOutputChannels = 15;

        private const int Mft1HeaderBytes = 48;   // through the 3×3 matrix
        private const int Mft2HeaderBytes = 52;   // …plus the two table-entry counts

        private readonly byte[] _data;
        private readonly int _clutOffset;
        private readonly bool _sixteenBit;
        private readonly int _grid;
        private readonly double[][] _inputCurves;    // [channel][entry]
        private readonly double[][] _outputCurves;
        private readonly bool _labPcs;
        /// <summary>
        /// The tag's 3×3 matrix, applied to the input before its curves — only for a table whose
        /// input is PCSXYZ (ICC.1: "the matrix is used only when the input colour space is
        /// PCSXYZ"), which in a PDF is the <c>B2A</c> of an XYZ-PCS profile. Null otherwise, and
        /// for the identity.
        /// </summary>
        private readonly double[] _inputMatrix;

        /// <summary>The v4 pipeline, when the tag was an <c>'mAB '</c> or <c>'mBA '</c>; null for the v2 forms.</summary>
        private readonly IccLutPipeline _pipeline;

        /// <summary>A device link's <c>A2B0</c>: device in, device out, every output read raw.</summary>
        private bool _deviceLink;

        /// <summary>
        /// What a PCSLAB sample of 1.0 means in L* terms, before the ×100.
        /// </summary>
        /// <remarks>
        /// <b>Three tag types, two encodings, and the difference is one count at the white point.</b>
        /// ICC.1 §6.3.4.2 gives legacy 16-bit PCSLAB <c>0xFF00</c> for L* = 100, so a
        /// <c>lut16Type</c> sample normalised by 65535 has to be scaled back up. <c>lut8Type</c>
        /// replicates its byte, so <c>0xFF</c> already <i>is</i> L* = 100. And <c>lutAToBType</c>
        /// uses the v4 encoding, where <c>0xFFFF</c> is L* = 100 — so it needs no scale either,
        /// and applying the legacy one would push every colour slightly past its lightness.
        /// Carrying the answer rather than deriving it from <c>_sixteenBit</c> is what keeps the
        /// third case from inheriting the first's rule.
        /// </remarks>
        private readonly double _labScale;

        private IccLut(byte[] data, int clutOffset, bool sixteenBit, int grid,
                       double[][] inputCurves, double[][] outputCurves, bool labPcs, double[] inputMatrix)
        {
            _data = data; _clutOffset = clutOffset; _sixteenBit = sixteenBit; _grid = grid;
            _inputCurves = inputCurves; _outputCurves = outputCurves; _labPcs = labPcs;
            _inputMatrix = inputMatrix;
            _labScale = sixteenBit ? 65535.0 / 65280.0 : 1.0;
        }

        private IccLut(IccLutPipeline pipeline, bool labPcs)
        {
            _pipeline = pipeline;
            _labPcs = labPcs;
            _sixteenBit = true;      // the PCS XYZ encoding is u1Fixed15 either way
            _labScale = 1.0;         // v4 PCSLAB: 0xFFFF is L* = 100
        }

        /// <summary>Device channels this table consumes.</summary>
        internal int InputChannels => _pipeline?.InputChannels ?? _inputCurves.Length;

        // Scratch for one evaluation. Plain arrays rather than stackalloc/Span because Base
        // multi-targets netstandard2.0, where Span<T> is not in the framework — the same
        // constraint Core.Text.UnicodeBidi records. [ThreadStatic] rather than instance fields
        // because one parsed profile is shared by every thread rendering the document, and rather
        // than locals because the image path evaluates this per pixel.
        [ThreadStatic] private static int[] _scratchBase;
        [ThreadStatic] private static double[] _scratchFrac;
        [ThreadStatic] private static double[] _scratchAcc;

        /// <summary>
        /// Device components in [0,1] → PCS, then PCS → sRGB.
        /// </summary>
        internal bool TryEvaluate(float[] components, out SKColor color)
        {
            color = default;

            if (_pipeline != null)
            {
                if (!_pipeline.TryEvaluate(components, out double a0, out double a1, out double a2))
                    return false;
                color = _labPcs ? DecodeLab(a0, a1, a2) : DecodeXyz(a0, a1, a2);
                return true;
            }

            // 1. + 2. Input curves and the multilinear interpolation, shared with TryEvaluateRaw.
            if (!TryInterpolateClut(components, out double[] acc, out int outputs)) return false;

            // 3. Output curves, then the PCS decoding the tag's own encoding implies.
            if (outputs < 3) return false;
            double p0 = Curve(_outputCurves[0], Clamp01(acc[0]));
            double p1 = Curve(_outputCurves[1], Clamp01(acc[1]));
            double p2 = Curve(_outputCurves[2], Clamp01(acc[2]));

            color = _labPcs ? DecodeLab(p0, p1, p2) : DecodeXyz(p0, p1, p2);
            return true;
        }

        /// <summary>Output channels this table produces, or 0 when it has no sampled table.</summary>
        internal int OutputChannels
            => _pipeline == null ? _outputCurves.Length : _pipeline.IsBToA || _deviceLink ? _pipeline.OutputChannels : 0;

        /// <summary>Whether this table's PCS is Lab rather than XYZ.</summary>
        internal bool IsLabPcs => _labPcs;

        /// <summary>
        /// The same interpolation, stopping at the output curves and writing every channel out
        /// rather than decoding three of them as a PCS colour.
        /// </summary>
        /// <remarks>
        /// <para><b>What a <c>B2A</c> needs, and the reason <see cref="TryEvaluate"/> could not
        /// serve it.</b> That method's last step decodes its first three outputs as a PCS encoding
        /// and returns an sRGB colour, which is right for <c>A2B</c> — device in, PCS out — and
        /// exactly wrong in the other direction, where the outputs are <i>device</i> components
        /// and there may be four of them.</para>
        ///
        /// <para><b>The pipeline itself is direction-agnostic</b>, which is why this is a split
        /// rather than a second implementation: input curves, a CLUT, output curves. A tag's type
        /// does not change with its direction; only the meaning of its numbers does.</para>
        ///
        /// <para>The v4 forms go to <see cref="IccLutPipeline"/>, which answers for an <c>mBA </c>,
        /// and for an <c>mAB </c> only when it is a device link's: a profile's <c>mAB </c> produces
        /// three PCS values by construction, so answering raw from it would be inventing channels,
        /// where a link's produces the device channels its header names.</para>
        /// </remarks>
        internal bool TryEvaluateRaw(float[] components, double[] outputs)
        {
            if (_pipeline != null)
                return _deviceLink ? _pipeline.TryEvaluateAToB(components, outputs) : _pipeline.TryEvaluateRaw(components, outputs);
            if (outputs == null || outputs.Length < _outputCurves.Length) return false;

            if (!TryInterpolateClut(components, out double[] acc, out int outCount)) return false;

            for (int k = 0; k < outCount; k++)
                outputs[k] = Curve(_outputCurves[k], Clamp01(acc[k]));

            return true;
        }

        /// <summary>
        /// The CLUT's own answer for <paramref name="components"/> — the input curves, then
        /// multilinear interpolation over the 2ⁿ corners of the cell they land in, accumulated into
        /// <paramref name="acc"/>. It stops <b>before</b> the output curves, which is the first step
        /// where the two directions stop agreeing.
        /// </summary>
        /// <remarks>
        /// <para><b>One implementation, two callers.</b> <see cref="TryEvaluate"/> decodes the first
        /// three accumulated channels as a PCS colour (A2B: device in, PCS out);
        /// <see cref="TryEvaluateRaw"/> writes every channel out through its own output curve (B2A:
        /// PCS in, device out). Everything up to <paramref name="acc"/> is one question, and each
        /// method used to carry its own answer to it — about thirty-five lines, character for
        /// character, grid clamp and row-major corner index included — under a remark on
        /// <c>TryEvaluateRaw</c> describing itself as "a split rather than a second implementation".
        /// It was a second implementation. A comment asserting that two bodies agree is a
        /// duplication report, and this one sat on an interpolation where a one-corner disagreement
        /// between the two directions is a colour shift no round-trip test could see, because a
        /// round trip that ran both copies would move both ends together.</para>
        /// <para><b>The shape is <see cref="IccLutPipeline"/>'s, not a new one</b> — that type has
        /// had a single <c>Interpolate</c> serving both of its directions all along. What keeps the
        /// two from merging further is the grid: this v2 form has one <c>_grid</c> for every axis and
        /// the v4 pipeline has one per axis, so the flat index accumulates a constant multiplier here
        /// and a per-axis one there. A cube and a box are the same code only when every axis agrees,
        /// so folding them together would invent a third shape rather than share one.</para>
        /// <para><paramref name="acc"/> is the <c>[ThreadStatic]</c> scratch buffer, so it is valid
        /// only until the next evaluation on this thread; both callers consume it before returning.
        /// <paramref name="outCount"/> is the table's output-channel count, which the callers check
        /// against their own needs — three for a PCS decode, the caller's buffer for a raw read.</para>
        /// </remarks>
        private bool TryInterpolateClut(float[] components, out double[] acc, out int outCount)
        {
            acc = null;
            outCount = 0;

            int n = InputChannels;
            if (components == null || components.Length < n) return false;

            int[] baseIndex = _scratchBase ??= new int[MaxInputChannels];
            double[] frac   = _scratchFrac ??= new double[MaxInputChannels];

            // An XYZ input goes through the tag's matrix first (n is 3 whenever it is set).
            double m0 = 0, m1 = 0, m2 = 0;
            if (_inputMatrix != null)
            {
                double x = Clamp01(components[0]), y = Clamp01(components[1]), z = Clamp01(components[2]);
                m0 = _inputMatrix[0] * x + _inputMatrix[1] * y + _inputMatrix[2] * z;
                m1 = _inputMatrix[3] * x + _inputMatrix[4] * y + _inputMatrix[5] * z;
                m2 = _inputMatrix[6] * x + _inputMatrix[7] * y + _inputMatrix[8] * z;
            }

            // Input curves, and the grid coordinate each channel lands on.
            for (int i = 0; i < n; i++)
            {
                double input = _inputMatrix == null ? components[i] : i == 0 ? m0 : i == 1 ? m1 : m2;
                double v = Curve(_inputCurves[i], Clamp01(input));
                double g = v * (_grid - 1);
                int i0 = SaturatingCast.ToInt(g);
                if (i0 >= _grid - 1) i0 = _grid - 2;
                if (i0 < 0) i0 = 0;
                baseIndex[i] = i0;
                frac[i] = g - i0;
            }

            // Multilinear interpolation over the 2^n corners of the enclosing cell. The weight of a
            // corner is the product of, per axis, the fraction or its complement — the same identity
            // PdfFunctionEvaluator's Type 0 interpolation uses.
            outCount = _outputCurves.Length;
            acc = _scratchAcc ??= new double[MaxOutputChannels];
            for (int k = 0; k < outCount; k++) acc[k] = 0.0;

            int corners = 1 << n;
            for (int corner = 0; corner < corners; corner++)
            {
                double weight = 1.0;
                int flat = 0;
                for (int i = 0; i < n; i++)
                {
                    bool high = (corner & (1 << i)) != 0;
                    weight *= high ? frac[i] : 1.0 - frac[i];
                    // Row-major with channel 0 varying slowest (ICC.1: "the first input channel
                    // varies least rapidly").
                    flat = flat * _grid + baseIndex[i] + (high ? 1 : 0);
                }
                if (weight == 0.0) continue;

                int sampleBase = flat * outCount;
                for (int k = 0; k < outCount; k++)
                    acc[k] += weight * ClutSample(sampleBase + k);
            }

            return true;
        }

        /// <summary>
        /// CIE L*a*b* → the [0,1] triple this table's input curves expect, the inverse of
        /// <see cref="DecodeLab(double, double, double)"/>.
        /// </summary>
        /// <remarks>
        /// Uses this table's own <c>_labScale</c>, so a legacy 16-bit encoding and a v4 one are
        /// each fed the numbers they were built for. Reading a table with the other one's scale is
        /// the 0.4 % lightness error <see cref="DecodeLab(double, double, double)"/> documents, in the other direction.
        /// </remarks>
        internal void EncodeLab(double l, double a, double b, float[] into)
        {
            double scale = _labScale;
            into[0] = (float)Clamp01(l / (100.0 * scale));
            into[1] = (float)Clamp01((a + 128.0) / (255.0 * scale));
            into[2] = (float)Clamp01((b + 128.0) / (255.0 * scale));
        }

        /// <summary>
        /// Legacy ICC Lab, which is <b>not</b> a plain 0..1 scaling and is the single easiest thing
        /// to get wrong here.
        ///
        /// <para>The encoding's landmarks are integers, not fractions: for 16-bit,
        /// <c>0xFF00</c> is L* = 100 and <c>0x8000</c> is a* = b* = 0, so full scale
        /// (<c>0xFFFF</c>) is slightly <i>past</i> L* = 100. That is why the constant is
        /// 65535/65280 rather than 1. The 8-bit form replicates a byte into both halves, which is
        /// the same encoding read at lower precision — hence the same landmarks.</para>
        ///
        /// <para><b>The error it prevents is one count, and that is worth saying plainly.</b>
        /// Multiplying by a plain 100 puts the PCS white at L* 99.61, which is sRGB 254 rather than
        /// 255 — invisible to a human and invisible to any pixel assertion with a tolerance above
        /// zero. <c>IccLutProfileTests.TheLabEncodingsWhitePoint_RendersAsWhite</c> therefore
        /// asserts <i>exact</i> white; a tolerance of 3 let the wrong constant through, which is
        /// how this note came to be written.</para>
        /// </summary>
        private SKColor DecodeLab(double v0, double v1, double v2)
        {
            DecodeLab(v0, v1, v2, out double l, out double a, out double b);
            return PdfColor.LabToSRgb((float)l, (float)a, (float)b);
        }

        /// <summary>
        /// This table's Lab PCS encoding read back as L*, a*, b* — the inverse of
        /// <see cref="EncodeLab"/>, for a caller that needs the numbers (black-point detection
        /// round-trips Lab) rather than an sRGB colour.
        /// </summary>
        internal void DecodeLab(double v0, double v1, double v2, out double l, out double a, out double b)
        {
            double scale = _labScale;
            l = v0 * 100.0 * scale;
            a = v1 * 255.0 * scale - 128.0;
            b = v2 * 255.0 * scale - 128.0;
        }

        /// <summary>
        /// XYZ PCS, encoded as u1Fixed15 — <c>0x8000</c> is 1.0, so full scale is just under 2.0.
        /// </summary>
        private static SKColor DecodeXyz(double v0, double v1, double v2)
        {
            const double Scale = 65535.0 / 32768.0;
            return PdfColor.XyzD50ToSRgb(v0 * Scale, v1 * Scale, v2 * Scale);
        }

        private double ClutSample(int index)
        {
            if (_sixteenBit)
            {
                int off = _clutOffset + index * 2;
                return ((_data[off] << 8) | _data[off + 1]) / 65535.0;
            }
            return _data[_clutOffset + index] / 255.0;
        }

        private static double Curve(double[] table, double x)
        {
            if (table == null || table.Length == 0) return x;
            if (table.Length == 1) return table[0];
            double pos = x * (table.Length - 1);
            int i = SaturatingCast.ToInt(pos);
            if (i >= table.Length - 1) return table[table.Length - 1];
            double f = pos - i;
            return table[i] + (table[i + 1] - table[i]) * f;
        }

        private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        // ==============================================================
        //  Parsing
        // ==============================================================

        /// <summary>
        /// Reads an <c>mft1</c>/<c>mft2</c> tag. Returns <c>null</c> with a reason for anything it
        /// cannot evaluate — including the v4 <c>mAB </c> form, which is a different layout rather
        /// than a variant of this one.
        /// </summary>
        /// <param name="labPcs">Whether the profile's PCS header field says <c>'Lab '</c>.</param>
        /// <param name="data">The whole ICC profile.</param>
        /// <param name="offset">Offset of the tag within <paramref name="data"/>.</param>
        /// <param name="size">Declared tag size in bytes.</param>
        /// <param name="reason">
        /// Why the tag could not be evaluated, or <c>null</c> on success. Non-null with a
        /// <c>null</c> return is what lets a caller report "profile present, fallback used" rather
        /// than staying silent — the distinction <c>IccTransformFailed</c> exists for.
        /// </param>
        /// <param name="bToA">
        /// Whether the tag is a <c>B2A</c> table, which decides the one v4 type it may be.
        /// </param>
        /// <param name="deviceLink">
        /// Whether the tag is a device link's <c>A2B0</c>: device channels in and out, one or more of
        /// them, read raw (<see cref="TryEvaluateRaw"/>) — no PCS on either side.
        /// </param>
        internal static IccLut Parse(byte[] data, int offset, int size, bool labPcs, out string reason,
                                     bool bToA = false, bool deviceLink = false)
        {
            IccLut lut = ParseTable(data, offset, size, labPcs, out reason, bToA, deviceLink);
            if (lut != null) lut._deviceLink = deviceLink;
            return lut;
        }

        private static IccLut ParseTable(byte[] data, int offset, int size, bool labPcs, out string reason,
                                         bool bToA, bool deviceLink)
        {
            reason = null;
            if (data == null || offset < 0 || size < 0 || (long)offset + size > data.Length)
            {
                reason = "A2B0 tag runs past the end of the profile";
                return null;
            }
            // Distinguished from the bound above on purpose: a tag that FITS but is too short to
            // hold a lut header is a different fact about the file, and the caller reading the
            // reason is trying to tell "malformed" from "a kind we do not evaluate".
            if (size < Mft1HeaderBytes)
            {
                reason = $"A2B0 tag is {size} bytes, too short for a lut8Type/lut16Type header";
                return null;
            }

            string type = Signature(data, offset);
            bool sixteenBit;
            switch (type)
            {
                case "mft1": sixteenBit = false; break;
                case "mft2": sixteenBit = true;  break;
                case "mAB ":
                case "mBA ":
                {
                    // A different layout rather than a variant, so it gets its own reader and its
                    // own PCS Lab encoding -- see IccLutPipeline and _labScale. Each v4 type runs in
                    // one direction only, so the tag it sits in has to agree with it.
                    bool isBToA = type == "mBA ";
                    if (isBToA != bToA)
                    {
                        reason = bToA
                            ? "B2A0 tag is an 'mAB ' lutAToBType, which runs device to PCS"
                            : "A2B0 tag is an 'mBA ' lutBToAType, which runs PCS to device";
                        return null;
                    }
                    IccLutPipeline pipeline = IccLutPipeline.Parse(data, offset, size, bToA, out reason, deviceLink);
                    return pipeline == null ? null : new IccLut(pipeline, labPcs);
                }
                default:
                    reason = $"A2B0 tag type is '{type}', not a lut8Type/lut16Type table";
                    return null;
            }

            int inputs  = data[offset + 8];
            int outputs = data[offset + 9];
            int grid    = data[offset + 10];

            if (inputs < 1 || inputs > MaxInputChannels)
            {
                reason = $"A2B0 declares {inputs} input channels; 1..{MaxInputChannels} is " +
                         "evaluated (interpolation visits 2^n corners)";
                return null;
            }
            // A2B's outputs are the PCS, so three; B2A's are device channels (one for grey), and its
            // inputs are the PCS; a device link's A2B0 has device channels on both sides.
            if (outputs < (bToA || deviceLink ? 1 : 3) || outputs > MaxOutputChannels)
            {
                reason = bToA || deviceLink
                    ? $"{(bToA ? "B2A0" : "A2B0")} declares {outputs} output channels; 1..{MaxOutputChannels} is evaluated"
                    : $"A2B0 declares {outputs} output channels; the PCS needs at least 3";
                return null;
            }
            if (bToA && inputs != 3)
            {
                reason = $"B2A0 declares {inputs} input channels; its input is the PCS, which has 3";
                return null;
            }
            // A grid of 1 point cannot be interpolated (the cell has no width) and a grid of 0 is
            // meaningless; both are rejected rather than clamped, because clamping would invent a
            // colour rather than decline one.
            if (grid < 2)
            {
                reason = $"A2B0 CLUT has {grid} grid point(s) per axis; 2 is the minimum";
                return null;
            }

            int headerBytes = sixteenBit ? Mft2HeaderBytes : Mft1HeaderBytes;
            // A lut16Type header is four bytes longer than the lut8Type one the bound above admits:
            // the two entry counts are read only from a tag that holds them, since a tag that ends
            // the profile has nothing after it to read.
            if (size < headerBytes)
            {
                reason = $"A2B0 tag is {size} bytes, too short for a lut16Type header";
                return null;
            }
            int inputEntries, outputEntries;
            if (sixteenBit)
            {
                inputEntries  = (data[offset + 48] << 8) | data[offset + 49];
                outputEntries = (data[offset + 50] << 8) | data[offset + 51];
                if (inputEntries < 2 || outputEntries < 2)
                {
                    reason = "A2B0 declares fewer than two entries in an input or output table";
                    return null;
                }
                if (inputEntries > PdfLimits.MaxIccCurvePoints || outputEntries > PdfLimits.MaxIccCurvePoints)
                {
                    reason = "A2B0 curve table exceeds the ICC curve-point cap";
                    return null;
                }
            }
            else
            {
                inputEntries = outputEntries = 256;   // lut8Type tables are fixed-length
            }

            // Total size, computed in `long` so a hostile grid/channel combination overflows the
            // check rather than the arithmetic. 2^8 channels at 255 grid points would be 255^8
            // samples, which is why this is checked before anything is allocated or indexed.
            int sampleBytes = sixteenBit ? 2 : 1;
            long clutSamples = outputs;
            for (int i = 0; i < inputs; i++)
            {
                clutSamples *= grid;
                if (clutSamples > PdfLimits.MaxIccProfileBytes) break;   // already impossible
            }

            long inputTableBytes  = (long)inputs  * inputEntries  * sampleBytes;
            long outputTableBytes = (long)outputs * outputEntries * sampleBytes;
            long clutBytes        = clutSamples * sampleBytes;
            long total = headerBytes + inputTableBytes + clutBytes + outputTableBytes;
            if (total > size)
            {
                reason = $"A2B0 declares {total} bytes of tables but the tag is {size}";
                return null;
            }

            int p = offset + headerBytes;
            double[][] inputCurves = ReadCurves(data, ref p, inputs, inputEntries, sixteenBit);
            int clutOffset = p;
            p += (int)clutBytes;
            double[][] outputCurves = ReadCurves(data, ref p, outputs, outputEntries, sixteenBit);
            double[] inputMatrix = bToA && !labPcs ? ReadMatrix(data, offset + 12) : null;

            return new IccLut(data, clutOffset, sixteenBit, grid, inputCurves, outputCurves, labPcs, inputMatrix);
        }

        private static double[][] ReadCurves(byte[] data, ref int p, int channels, int entries,
                                             bool sixteenBit)
        {
            var curves = new double[channels][];
            for (int c = 0; c < channels; c++)
            {
                var table = new double[entries];
                for (int e = 0; e < entries; e++)
                {
                    table[e] = sixteenBit
                        ? ((data[p] << 8) | data[p + 1]) / 65535.0
                        : data[p] / 255.0;
                    p += sixteenBit ? 2 : 1;
                }
                curves[c] = table;
            }
            return curves;
        }

        /// <summary>
        /// The lut header's nine s15Fixed16 matrix entries (row-major, from byte 12), or null when
        /// they are the identity — the common case, kept off the per-colour path.
        /// </summary>
        private static double[] ReadMatrix(byte[] data, int at)
        {
            var m = new double[9];
            bool identity = true;
            for (int i = 0; i < 9; i++)
            {
                int o = at + i * 4;
                int raw = (data[o] << 24) | (data[o + 1] << 16) | (data[o + 2] << 8) | data[o + 3];
                m[i] = raw / 65536.0;
                if (m[i] != (i % 4 == 0 ? 1.0 : 0.0)) identity = false;
            }
            return identity ? null : m;
        }

        private static string Signature(byte[] data, int offset)
        {
            var chars = new char[4];
            for (int i = 0; i < 4; i++)
            {
                byte b = data[offset + i];
                chars[i] = b >= 32 && b < 127 ? (char)b : '?';
            }
            return new string(chars);
        }
    }
}
