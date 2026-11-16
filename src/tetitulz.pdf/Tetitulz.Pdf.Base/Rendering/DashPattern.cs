// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// The dash pattern of §8.4.3.6 — reading the <c>d</c> operator, the effective repeat period,
    /// and the phase normalisation. <b>One implementation, both renderers.</b>
    ///
    /// <para><b>The period and phase functions were byte-identical copies</b>, and the SVG one
    /// carried a comment reading "Same function as <c>PdfRenderContext.NormaliseDashPhase</c>".
    /// That sentence is the marker this repository has learned to distrust: nothing enforces prose,
    /// and every rendering defect the project has found was one renderer lagging the other.</para>
    ///
    /// <para><b>Reading the operator was <i>not</i> identical, which is why the copies mattered.</b>
    /// The raster handler required both operands and ignored the operator otherwise; the SVG one
    /// accepted the array alone and supplied a phase of 0. So <c>d [3 3]</c> with the phase missing
    /// left the previous dash in force in one renderer and installed a new one in the other. The
    /// conservative reading is now shared: §8.4.3.6 defines <c>d</c> as taking two operands, so a
    /// stream carrying one has not written a <c>d</c>, and the graphics state is left alone rather
    /// than being changed on a guess.</para>
    ///
    /// <para><b>No static fields</b>, only pure functions — the same leaf discipline
    /// <c>SvgSyntax</c> and <c>TransferFunctionLut</c> are held to, so sharing it between the two
    /// renderers adds no type-initialization edge.</para>
    /// </summary>
    internal static class DashPattern
    {
        /// <summary>
        /// Reads <c>d [array] phase</c>. Returns <c>false</c> — leaving the graphics state
        /// untouched — when the operator does not carry both operands or the first is not an array.
        /// An empty array is the spec's "solid line": <paramref name="pattern"/> comes back
        /// <c>null</c> and <paramref name="phase"/> 0.
        /// </summary>
        internal static bool TryRead(ContentStreamOperation op, out float[] pattern, out float phase)
        {
            pattern = null;
            phase = 0f;

            if (op.Operands.Length < 2 || op.Operands[0] is not PdfArray arr) return false;

            if (arr.Count == 0)
            {
                // §8.4.3.6: an empty array turns dashing off. The phase is meaningless then, and
                // storing the operand's value would leave a stale offset to be applied by whatever
                // `d` comes next. The raster path used to keep it; the SVG path zeroed it.
                return true;
            }

            var values = new float[arr.Count];
            for (int i = 0; i < arr.Count; i++) values[i] = ContentOperand.ArrayFloat(arr, i);

            pattern = values;
            phase = ContentOperand.Float(op, 1);
            return true;
        }

        /// <summary>
        /// Reads the ExtGState <c>/D</c> entry — <c>[dashArray phase]</c>, §8.4.3.6 and Table 58.
        /// Returns <c>false</c>, leaving the graphics state untouched, when the entry is missing or
        /// is not the two-element shape the table defines. An empty array is the spec's "solid
        /// line": <paramref name="pattern"/> comes back <c>null</c> and <paramref name="phase"/> 0.
        /// </summary>
        /// <remarks>
        /// <para><b>Every element is resolved, and that is the point of this method existing.</b>
        /// The outer array, the inner array, each of the inner array's <i>elements</i>, and the
        /// phase may all be written as indirect references. Resolving only the outer two would read an
        /// indirect dash length as <b>0</b> and an indirect phase as 0 — and with a reader per
        /// renderer, one renderer ahead of the other. Both read through here.</para>
        ///
        /// <para><b>The empty-array case zeroes the phase.</b> §8.4.3.6 makes the phase meaningless
        /// when dashing is off, and keeping the operand's value leaves a stale offset in the
        /// graphics state — for the ExtGState path as for <see cref="TryRead"/> and the <c>d</c>
        /// operator.</para>
        /// </remarks>
        internal static bool TryReadExtGState(
            PdfDocument doc, PdfDictionary extGs, out float[] pattern, out float phase)
        {
            pattern = null;
            phase = 0f;

            if (PdfResolve.Array(doc, extGs, PdfName.D) is not PdfArray entry || entry.Count < 2)
                return false;
            if (PdfResolve.Array(doc, entry[0]) is not PdfArray arr) return false;

            if (arr.Count == 0) return true;

            var values = new float[arr.Count];
            for (int i = 0; i < arr.Count; i++)
                values[i] = (float)(PdfResolve.Number(doc, arr[i])?.DoubleValue ?? 0.0);

            float readPhase = (float)(PdfResolve.Number(doc, entry[1])?.DoubleValue ?? 0.0);
            // A value past float's range ignores the entry, as a `d` with such an operand is ignored.
            if (!MatrixGuard.IsFinite(readPhase) || !MatrixGuard.AllFinite(values)) return false;
            pattern = values;
            phase = readPhase;
            return true;
        }

        /// <summary>
        /// The effective repeat period. §8.4.3.6: an odd-length array is conceptually repeated to
        /// make it even, so the on/off roles swap on each repeat and the true period is
        /// <b>twice</b> the sum. An even-length array repeats every sum.
        /// </summary>
        /// <remarks>
        /// SVG applies the same doubling rule to an odd-length <c>stroke-dasharray</c>, which is why
        /// the exported array itself needs no expansion — only the phase needs the right period.
        /// Skia does not, which is why <see cref="ToEvenLengthScaled"/> exists for the raster path.
        /// </remarks>
        internal static float EffectiveDashPeriod(float[] pattern)
        {
            if (pattern == null || pattern.Length == 0) return 0f;
            float sum = 0f;
            foreach (float v in pattern) sum += v;
            return pattern.Length % 2 == 0 ? sum : 2f * sum;
        }

        /// <summary>
        /// Normalises a phase into <c>[0, period)</c>. ISO 32000-2 §8.4.3.6: "If the dash phase is
        /// negative, it shall be incremented by twice the sum of all lengths in the dash array
        /// until it is positive."
        /// </summary>
        /// <remarks>
        /// Reducing modulo the plain sum rather than the <see cref="EffectiveDashPeriod"/> lands an
        /// odd-length array half a period off and <i>inverts</i> its dashes — gaps where the ink
        /// should be. That is the whole reason the period is a function rather than a sum.
        /// </remarks>
        internal static float NormalisePhase(float phase, float[] pattern)
        {
            if (phase >= 0f) return phase;
            float cycle = EffectiveDashPeriod(pattern);
            if (cycle <= 0f) return 0f;
            float normalised = phase % cycle;
            return normalised < 0f ? normalised + cycle : normalised;
        }

        /// <summary>
        /// The array as Skia needs it: even length (an odd one repeated once), scaled by the CTM,
        /// and with negative entries — invalid in PDF — clamped to zero.
        /// </summary>
        internal static float[] ToEvenLengthScaled(float[] pattern, float scale)
        {
            int n = pattern.Length % 2 == 0 ? pattern.Length : pattern.Length * 2;
            var scaled = new float[n];
            for (int i = 0; i < n; i++)
                scaled[i] = Math.Max(0f, pattern[i % pattern.Length] * scale);
            return scaled;
        }
    }
}
