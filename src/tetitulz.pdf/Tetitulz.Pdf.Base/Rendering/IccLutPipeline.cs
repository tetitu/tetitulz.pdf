// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// ICC.1 §10.12 <c>lutAToBType</c> (<c>'mAB '</c>) and §10.13 <c>lutBToAType</c> (<c>'mBA '</c>) —
    /// the v4 table forms, evaluated as the pipelines the spec defines: A curves → CLUT → M curves →
    /// matrix → B curves for AToB, and B curves → matrix → M curves → CLUT → A curves for BToA.
    /// </summary>
    /// <remarks>
    /// <para><b>A different layout, not a variant of <c>lut16Type</c>.</b> Every stage is optional
    /// except the B curves, the curves are <i>tagged elements</i> rather than sampled tables inline
    /// in the header, and the CLUT carries a grid size <b>per input channel</b> where the v2 forms
    /// carry one for all of them. That last one is why this cannot share
    /// <see cref="IccLut"/>'s interpolation: a cube and a box are the same code only when every
    /// edge is the same length.</para>
    ///
    /// <para><b>One class for both directions, because the two types share everything but the
    /// order.</b> ICC.1:2022 Table 47 gives <c>lutBToAType</c> the same header and the same five
    /// element offsets at the same byte positions as Table 45's <c>lutAToBType</c>, the same CLUT
    /// encoding, and the four permitted element combinations mirrored. What changes is the order
    /// the elements run in and which channel count each curve set follows: B and M curves follow
    /// the <i>output</i> channels in AToB and the <i>input</i> channels in BToA, A curves the
    /// reverse. A second class would be a second copy of the parser, the curve reader and the
    /// per-axis interpolation, differing in three counts.</para>
    ///
    /// <para><b>The PCS Lab encoding is the v4 one, and that is the trap.</b> ICC.1 §6.3.4.2 gives
    /// legacy 16-bit PCSLAB <c>0xFF00</c> for L* = 100, which is what <c>lut16Type</c> uses and what
    /// <see cref="IccLut"/>'s decoder scales for. The v4 types use the v4 encoding, where
    /// <c>0xFFFF</c> is L* = 100 — so applying the legacy scale here would put every colour 0.4%
    /// past its intended lightness.</para>
    ///
    /// <para><b>And at the white point that error is worth exactly nothing, which is the opposite
    /// of what the v2 form's note says about its own.</b> The scale pushes L* <i>up</i>, so at full
    /// scale it lands on 100.39 and sRGB clamps it to the same white as 100.0; the v2 test detects
    /// its own missing scale only because dropping it lands on 99.61, and a count <i>below</i>
    /// white is visible. A fixture for this one therefore has to sit below white — the first
    /// version of the test did not, and mutating the legacy scale back in survived it.</para>
    ///
    /// <para><b>Only the four element combinations ICC.1 permits are evaluated</b> — B alone;
    /// B with M and the matrix; B with the CLUT and A; and all five. Anything else is refused by
    /// name rather than evaluated as if the missing stage were an identity, because a profile that
    /// reached here with a CLUT and no A curves is a profile this does not understand and guessing
    /// is how a colour silently shifts.</para>
    ///
    /// <para><b>Bounds come from the tag's own declared size</b> rather than from a new constant:
    /// the CLUT's extent is the product of its grid sizes, which the file chooses, so it is
    /// accumulated in <c>long</c> and checked against the bytes actually present. The input-channel
    /// count still uses <see cref="IccLut.MaxInputChannels"/>, because interpolation visits 2ⁿ
    /// corners and that is an exponent.</para>
    /// </remarks>
    internal sealed class IccLutPipeline
    {
        /// <summary>Through the five element offsets.</summary>
        private const int HeaderBytes = 32;

        /// <summary>ICC.1 §10.12.5 / §10.13.5: sixteen grid-size bytes, a precision byte, three reserved.</summary>
        private const int ClutHeaderBytes = 20;

        /// <summary>The PCS has three components, whichever side of the table it is on.</summary>
        private const int PcsChannels = 3;

        private readonly byte[] _data;
        private readonly IccCurve[] _a;        // null when the stage is absent
        private readonly IccCurve[] _m;
        private readonly IccCurve[] _b;        // never null
        private readonly double[] _matrix;     // null, else 9 coefficients then 3 offsets
        private readonly int _clutOffset;      // -1 when absent
        private readonly int[] _grid;          // one size per input channel; null with no CLUT
        private readonly int _precision;       // bytes per sample: 1 or 2

        private IccLutPipeline(byte[] data, bool bToA, int inputs, int outputs, IccCurve[] a,
                               IccCurve[] m, IccCurve[] b, double[] matrix, int clutOffset,
                               int[] grid, int precision)
        {
            _data = data;
            _a = a; _m = m; _b = b;
            _matrix = matrix;
            _clutOffset = clutOffset;
            _grid = grid;
            _precision = precision;
            IsBToA = bToA;
            InputChannels = inputs;
            OutputChannels = outputs;
        }

        /// <summary>Whether this is an <c>'mBA '</c> (PCS → device) rather than an <c>'mAB '</c>.</summary>
        internal bool IsBToA { get; }

        /// <summary>Channels this pipeline consumes: device channels for AToB, the PCS for BToA.</summary>
        internal int InputChannels { get; }

        /// <summary>Components the pipeline produces: the PCS (first three) for AToB, device channels for BToA.</summary>
        internal int OutputChannels { get; }

        // One evaluation's working space. Plain arrays rather than stackalloc/Span because Base
        // multi-targets netstandard2.0; [ThreadStatic] because one parsed profile is shared by
        // every thread rendering the document and the image path evaluates this per pixel.
        [ThreadStatic] private static int[] _scratchBase;
        [ThreadStatic] private static double[] _scratchFrac;
        [ThreadStatic] private static double[] _scratchIn;
        [ThreadStatic] private static double[] _scratchOut;
        [ThreadStatic] private static double[] _scratchPcs;

        /// <summary>
        /// AToB: device components in [0,1] → the three PCS components, in the PCS's own [0,1]
        /// encoding. False for a BToA pipeline.
        /// </summary>
        /// <remarks>
        /// The PCS values are handed back rather than converted here, because whether they are Lab
        /// or XYZ is the <i>profile header's</i> answer and not this tag's — the same split
        /// <see cref="IccLut"/> makes.
        /// </remarks>
        internal bool TryEvaluate(float[] components, out double p0, out double p1, out double p2)
        {
            p0 = p1 = p2 = 0.0;
            double[] pcs = _scratchPcs ??= new double[MaxOutputChannels];
            if (OutputChannels < PcsChannels || !TryEvaluateAToB(components, pcs)) return false;
            p0 = pcs[0]; p1 = pcs[1]; p2 = pcs[2];
            return true;
        }

        /// <summary>
        /// AToB with every output written to <paramref name="result"/>, in [0,1]: a profile's three
        /// PCS components, or a device link's device channels (ICC.1 §10.12.1's order — A curves, CLUT,
        /// M curves, matrix, B curves — whatever the outputs mean). False for a BToA pipeline.
        /// </summary>
        internal bool TryEvaluateAToB(float[] components, double[] result)
        {
            if (IsBToA) return false;

            int n = InputChannels;
            if (components == null || components.Length < n) return false;
            if (result == null || result.Length < OutputChannels) return false;

            double[] inputs = _scratchIn ??= new double[IccLut.MaxInputChannels];
            double[] outputs = _scratchOut ??= new double[MaxOutputChannels];

            for (int i = 0; i < n; i++) inputs[i] = Clamp01(components[i]);

            // 1. A curves, then the CLUT. ICC.1 pairs them and Parse refuses a tag that does not,
            //    so a CLUT here means A curves here -- no null check, because a guard the
            //    constructor's own invariant excludes would read as though the pairing were
            //    merely likely.
            if (_clutOffset >= 0)
            {
                for (int i = 0; i < n; i++) inputs[i] = _a[i].Evaluate(inputs[i]);
                Interpolate(inputs, outputs);
            }
            else
            {
                // No CLUT: the channels pass straight through, which ICC.1 only permits when the
                // counts match.
                for (int k = 0; k < OutputChannels; k++) outputs[k] = inputs[k];
            }

            // 2. M curves and the matrix, which ICC.1 permits only together and only for a
            //    three-component PCS.
            if (_m != null)
                for (int k = 0; k < OutputChannels; k++) outputs[k] = _m[k].Evaluate(outputs[k]);

            if (_matrix != null)
                ApplyMatrix(outputs);

            // 3. B curves. Mandatory, so there is no identity fallback to get wrong.
            for (int k = 0; k < OutputChannels; k++) result[k] = _b[k].Evaluate(Clamp01(outputs[k]));
            return true;
        }

        /// <summary>
        /// BToA: the three PCS components in their [0,1] encoding → device components in [0,1],
        /// written to <paramref name="outputs"/>. False for an AToB pipeline.
        /// </summary>
        /// <remarks>
        /// ICC.1:2022 §10.13.1's order — B curves, matrix, M curves, CLUT, A curves — with §10.13.3's
        /// clip of the matrix results to [0,1] before they reach the M curves.
        /// </remarks>
        internal bool TryEvaluateRaw(float[] components, double[] outputs)
        {
            if (!IsBToA) return false;

            int n = InputChannels;
            if (components == null || components.Length < n) return false;
            if (outputs == null || outputs.Length < OutputChannels) return false;

            double[] stage = _scratchIn ??= new double[IccLut.MaxInputChannels];
            double[] clut = _scratchOut ??= new double[MaxOutputChannels];

            // 1. B curves. Mandatory.
            for (int i = 0; i < n; i++) stage[i] = _b[i].Evaluate(Clamp01(components[i]));

            // 2. The matrix, clipped, then the M curves — paired by Parse, three channels only.
            if (_matrix != null)
            {
                ApplyMatrix(stage);
                for (int i = 0; i < n; i++) stage[i] = _m[i].Evaluate(Clamp01(stage[i]));
            }

            // 3. The CLUT, then the A curves — also paired by Parse.
            if (_clutOffset >= 0)
            {
                Interpolate(stage, clut);
                for (int k = 0; k < OutputChannels; k++) outputs[k] = _a[k].Evaluate(Clamp01(clut[k]));
            }
            else
            {
                // No CLUT: the channel counts match, and the values pass straight through.
                for (int k = 0; k < OutputChannels; k++) outputs[k] = Clamp01(stage[k]);
            }

            return true;
        }

        /// <summary>ICC.1's 3×3 matrix with offsets, in place on the first three values.</summary>
        private void ApplyMatrix(double[] values)
        {
            double x = values[0], y = values[1], z = values[2];
            values[0] = (_matrix[0] * x) + (_matrix[1] * y) + (_matrix[2] * z) + _matrix[9];
            values[1] = (_matrix[3] * x) + (_matrix[4] * y) + (_matrix[5] * z) + _matrix[10];
            values[2] = (_matrix[6] * x) + (_matrix[7] * y) + (_matrix[8] * z) + _matrix[11];
        }

        /// <summary>
        /// Multilinear interpolation over the 2ⁿ corners of the cell the inputs land in.
        /// </summary>
        /// <remarks>
        /// <b>Each axis has its own length</b>, which is the whole difference from
        /// <see cref="IccLut"/>'s cube: the flat index accumulates <c>flat * grid[i] + index</c>
        /// with a <i>per-axis</i> multiplier, and ICC.1's "the first input channel varies least
        /// rapidly" fixes which axis is which.
        /// </remarks>
        private void Interpolate(double[] inputs, double[] outputs)
        {
            int n = InputChannels;
            int outs = OutputChannels;

            int[] baseIndex = _scratchBase ??= new int[IccLut.MaxInputChannels];
            double[] frac = _scratchFrac ??= new double[IccLut.MaxInputChannels];

            for (int i = 0; i < n; i++)
            {
                double g = inputs[i] * (_grid[i] - 1);
                int i0 = SaturatingCast.ToInt(g);
                if (i0 >= _grid[i] - 1) i0 = _grid[i] - 2;
                if (i0 < 0) i0 = 0;
                baseIndex[i] = i0;
                frac[i] = g - i0;
            }

            for (int k = 0; k < outs; k++) outputs[k] = 0.0;

            int corners = 1 << n;
            for (int corner = 0; corner < corners; corner++)
            {
                double weight = 1.0;
                int flat = 0;
                for (int i = 0; i < n; i++)
                {
                    bool high = (corner & (1 << i)) != 0;
                    weight *= high ? frac[i] : 1.0 - frac[i];
                    flat = (flat * _grid[i]) + baseIndex[i] + (high ? 1 : 0);
                }
                if (weight == 0.0) continue;

                int sampleBase = flat * outs;
                for (int k = 0; k < outs; k++) outputs[k] += weight * Sample(sampleBase + k);
            }
        }

        private double Sample(int index)
        {
            if (_precision == 2)
            {
                int off = _clutOffset + (index * 2);
                return ((_data[off] << 8) | _data[off + 1]) / 65535.0;
            }
            return _data[_clutOffset + index] / 255.0;
        }

        private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        /// <summary>
        /// Largest output-channel count. Matches <see cref="IccLut"/>'s: the PCS needs three and the
        /// headroom keeps a profile with a padded table readable.
        /// </summary>
        private const int MaxOutputChannels = 15;

        // ==============================================================
        //  Parsing
        // ==============================================================

        /// <summary>
        /// Reads an <c>'mAB '</c> or <c>'mBA '</c> tag, or returns <c>null</c> with a reason.
        /// </summary>
        /// <param name="data">The whole ICC profile.</param>
        /// <param name="offset">Offset of the tag within <paramref name="data"/>.</param>
        /// <param name="size">Declared tag size in bytes.</param>
        /// <param name="bToA">True for <c>'mBA '</c>, false for <c>'mAB '</c>; the caller has checked the signature.</param>
        /// <param name="reason">Why the tag could not be evaluated, or <c>null</c> on success.</param>
        /// <param name="deviceLink">
        /// An AToB table of a device link, whose outputs are device channels — one or more, where a
        /// profile's are the three of the PCS.
        /// </param>
        internal static IccLutPipeline Parse(byte[] data, int offset, int size, bool bToA, out string reason,
                                             bool deviceLink = false)
        {
            reason = null;
            string name = bToA ? "B2A0 'mBA '" : "A2B0 'mAB '";
            string clause = bToA ? "§10.13" : "§10.12";

            if (size < HeaderBytes)
            {
                reason = $"{name} tag is {size} bytes, too short for its element offsets";
                return null;
            }

            int inputs = data[offset + 8];
            int outputs = data[offset + 9];

            if (bToA)
            {
                if (inputs != PcsChannels)
                {
                    reason = $"{name} declares {inputs} input channels; its input is the three-component PCS";
                    return null;
                }
                if (outputs < 1 || outputs > MaxOutputChannels)
                {
                    reason = $"{name} declares {outputs} output channels; 1..{MaxOutputChannels} is evaluated";
                    return null;
                }
            }
            else
            {
                if (inputs < 1 || inputs > IccLut.MaxInputChannels)
                {
                    reason = $"{name} declares {inputs} input channels; 1..{IccLut.MaxInputChannels} " +
                             "is evaluated (interpolation visits 2^n corners)";
                    return null;
                }
                if (outputs < (deviceLink ? 1 : PcsChannels) || outputs > MaxOutputChannels)
                {
                    reason = deviceLink
                        ? $"{name} declares {outputs} output channels; 1..{MaxOutputChannels} is evaluated"
                        : $"{name} declares {outputs} output channels; the PCS needs at least 3";
                    return null;
                }
            }

            // Element offsets are from the start of the TAG, and zero means "this stage is absent".
            long bOffset = ReadU32(data, offset + 12);
            long matrixOffset = ReadU32(data, offset + 16);
            long mOffset = ReadU32(data, offset + 20);
            long clutOffset = ReadU32(data, offset + 24);
            long aOffset = ReadU32(data, offset + 28);

            foreach (long o in new[] { bOffset, matrixOffset, mOffset, clutOffset, aOffset })
            {
                if (o != 0 && (o < HeaderBytes || o >= size))
                {
                    reason = $"an {name} element offset falls outside the tag";
                    return null;
                }
            }

            if (bOffset == 0)
            {
                reason = $"{name} has no B curves, which ICC.1 {clause} requires";
                return null;
            }

            // ICC.1 permits four shapes, and pairs the optional stages. Refusing the others by name
            // beats evaluating a missing stage as an identity: a CLUT with no A curves is a profile
            // this does not understand, and the wrong answer would be a plausible colour.
            if ((clutOffset == 0) != (aOffset == 0))
            {
                reason = $"{name} has a CLUT without A curves, or A curves without a CLUT; " +
                         $"ICC.1 {clause} pairs them";
                return null;
            }
            if ((matrixOffset == 0) != (mOffset == 0))
            {
                reason = $"{name} has a matrix without M curves, or M curves without a matrix; " +
                         $"ICC.1 {clause} pairs them";
                return null;
            }
            if (clutOffset == 0 && inputs != outputs)
            {
                reason = $"{name} has no CLUT but {inputs} inputs and {outputs} outputs; " +
                         "without a table there is nothing to change the channel count";
                return null;
            }

            // The B and M curves sit on the PCS side of the table: its outputs for AToB, its inputs
            // for BToA. The A curves sit on the device side.
            int pcsSide = bToA ? inputs : outputs;
            int deviceSide = bToA ? outputs : inputs;

            if (matrixOffset != 0 && pcsSide != PcsChannels)
            {
                reason = $"{name} has a matrix over {pcsSide} channels; ICC.1 {clause} defines " +
                         "it for three";
                return null;
            }

            IccCurve[] b = ReadCurves(data, offset, size, (int)bOffset, pcsSide, name, "B", out reason);
            if (b == null) return null;

            IccCurve[] a = null;
            if (aOffset != 0)
            {
                a = ReadCurves(data, offset, size, (int)aOffset, deviceSide, name, "A", out reason);
                if (a == null) return null;
            }

            IccCurve[] m = null;
            if (mOffset != 0)
            {
                m = ReadCurves(data, offset, size, (int)mOffset, pcsSide, name, "M", out reason);
                if (m == null) return null;
            }

            double[] matrix = null;
            if (matrixOffset != 0)
            {
                if (matrixOffset + 48 > size)
                {
                    reason = $"{name} matrix runs past the end of the tag";
                    return null;
                }
                matrix = new double[12];
                for (int i = 0; i < 12; i++)
                    matrix[i] = ReadS15Fixed16(data, offset + (int)matrixOffset + (i * 4));
            }

            int clutDataOffset = -1;
            int[] grid = null;
            int precision = 0;

            if (clutOffset != 0)
            {
                if (clutOffset + ClutHeaderBytes > size)
                {
                    reason = $"{name} CLUT header runs past the end of the tag";
                    return null;
                }

                int clutBase = offset + (int)clutOffset;
                grid = new int[inputs];
                long entries = 1;

                for (int i = 0; i < inputs; i++)
                {
                    grid[i] = data[clutBase + i];
                    if (grid[i] < 2)
                    {
                        reason = $"{name} CLUT axis {i} has {grid[i]} grid points; a cell " +
                                 "needs two to interpolate between";
                        return null;
                    }
                    entries *= grid[i];
                    if (entries > size)
                    {
                        reason = $"{name} CLUT grid is larger than the tag that holds it";
                        return null;
                    }
                }

                precision = data[clutBase + 16];
                if (precision != 1 && precision != 2)
                {
                    reason = $"{name} CLUT precision byte is {precision}; ICC.1 defines 1 and 2";
                    return null;
                }

                long bytes = entries * outputs * precision;
                if (clutOffset + ClutHeaderBytes + bytes > size)
                {
                    reason = $"{name} CLUT data runs past the end of the tag";
                    return null;
                }

                clutDataOffset = clutBase + ClutHeaderBytes;
            }

            reason = null;
            return new IccLutPipeline(data, bToA, inputs, outputs, a, m, b, matrix,
                                      clutDataOffset, grid, precision);
        }

        /// <summary>
        /// Reads <paramref name="count"/> consecutive curve elements starting at
        /// <paramref name="elementOffset"/> bytes into the tag.
        /// </summary>
        /// <remarks>
        /// <b>Each element is padded to a four-byte boundary before the next</b> (ICC.1 §7.2.5), so
        /// the stride is not the element's own length. Reading them back to back without the
        /// padding walks off by one to three bytes per curve and lands the next signature check in
        /// the middle of a table, which is a decode failure rather than a wrong colour — but only
        /// for profiles whose curves happen to be misaligned, which is most of them and not all.
        /// </remarks>
        private static IccCurve[] ReadCurves(byte[] data, int tagOffset, int tagSize,
                                             int elementOffset, int count, string name,
                                             string which, out string reason)
        {
            var curves = new IccCurve[count];
            int p = elementOffset;

            for (int i = 0; i < count; i++)
            {
                int available = tagSize - p;
                if (available <= 0)
                {
                    reason = $"{name} {which} curve {i} starts past the end of the tag";
                    return null;
                }

                IccCurve curve = IccProfile.ReadPipelineCurve(
                    data, tagOffset + p, available, out int consumed, out reason);
                if (curve == null)
                {
                    reason = $"{name} {which} curve {i}: {reason}";
                    return null;
                }

                curves[i] = curve;
                p += consumed;
            }

            reason = null;
            return curves;
        }

        private static uint ReadU32(byte[] d, int o) => Core.IO.BigEndian.U32(d, o);

        private static double ReadS15Fixed16(byte[] d, int o)
            => (int)ReadU32(d, o) / 65536.0;
    }
}
