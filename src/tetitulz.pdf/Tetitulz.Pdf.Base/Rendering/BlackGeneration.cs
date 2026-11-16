// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using SkiaSharp;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Functions;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// The graphics state's black generation and undercolour removal (<c>/BG</c>, <c>/BG2</c>,
    /// <c>/UCR</c>, <c>/UCR2</c> — ISO 32000-1 §10.3.4, Table 58): how a DeviceRGB colour becomes
    /// the four process inks a separation plate lays down.
    /// </summary>
    /// <remarks>
    /// <para>§10.3.4: c = 1 − red, m = 1 − green, y = 1 − blue, k = min(c, m, y); then each of c, m
    /// and y less UCR(k), and k = BG(k), every result clamped to [0, 1]. Only a DeviceRGB colour is
    /// converted so — a CIE-based or ICC colour, and any colour a CMYK output profile converts, is
    /// not — and only where the device is CMYK, which here is a separation plate; the composite
    /// render composites sRGB and is untouched.</para>
    /// <para>With neither set, or both <c>/Default</c>, there is no instance, and the plates keep
    /// the device's own conversion, the exact inverse of <see cref="PdfColor.CmykToRgb"/> (full
    /// black generation, the inks normalised by 1 − k, so the recombined plates give back the
    /// colour). One set and the other <c>/Default</c> or absent: the absent one is the identity
    /// (BG(k) = k, UCR(k) = k), the full removal §10.3.4's formula reads it as.</para>
    /// <para>Each function is sampled once, at 256 inputs, when the <c>gs</c> sets it — inside the
    /// operator loop, so its evaluations spend the page's function budget.</para>
    /// </remarks>
    internal sealed class BlackGeneration
    {
        private const int Samples = 256;

        private readonly float[] _blackGeneration;   // null: BG(k) = k
        private readonly float[] _undercolorRemoval; // null: UCR(k) = k

        private BlackGeneration(float[] blackGeneration, float[] undercolorRemoval)
        {
            _blackGeneration = blackGeneration;
            _undercolorRemoval = undercolorRemoval;
        }

        /// <summary>
        /// The state an ExtGState leaves: <c>/BG2</c> over <c>/BG</c> and <c>/UCR2</c> over
        /// <c>/UCR</c>, a key the dictionary does not hold keeping <paramref name="current"/>'s
        /// function; null when both functions are the default.
        /// </summary>
        internal static BlackGeneration Read(PdfDocument doc, PdfDictionary extGs, BlackGeneration current)
        {
            PdfObject bg = PdfResolve.Value(doc, extGs[PdfName.BG2] ?? extGs[PdfName.BG]);
            PdfObject ucr = PdfResolve.Value(doc, extGs[PdfName.UCR2] ?? extGs[PdfName.UCR]);
            if (bg == null && ucr == null) return current;

            float[] blackGeneration = bg == null ? current?._blackGeneration : Sample(bg, doc, clampBelow: 0f);
            float[] undercolorRemoval = ucr == null ? current?._undercolorRemoval : Sample(ucr, doc, clampBelow: -1f);
            return blackGeneration == null && undercolorRemoval == null
                ? null
                : new BlackGeneration(blackGeneration, undercolorRemoval);
        }

        /// <summary>A DeviceRGB colour's process inks (§10.3.4).</summary>
        internal void ToCmyk(SKColor rgb, out float c, out float m, out float y, out float k)
        {
            c = 1f - rgb.Red / 255f;
            m = 1f - rgb.Green / 255f;
            y = 1f - rgb.Blue / 255f;
            float black = Math.Min(c, Math.Min(m, y));
            int at = PdfColor.Channel(black);
            float removed = _undercolorRemoval != null ? _undercolorRemoval[at] : black;
            c = PdfColor.Clamp01(c - removed);
            m = PdfColor.Clamp01(m - removed);
            y = PdfColor.Clamp01(y - removed);
            k = PdfColor.Clamp01(_blackGeneration != null ? _blackGeneration[at] : black);
        }

        /// <summary>
        /// One function's 256 samples over [0, 1], or null for <c>/Default</c>, a name of any
        /// kind, or a function of no type §7.10 defines — each of which leaves the identity.
        /// </summary>
        private static float[] Sample(PdfObject entry, PdfDocument doc, float clampBelow)
        {
            if (!(entry is PdfDictionary fn)) return null;
            long type = PdfResolve.Number(doc, fn, PdfName.FunctionType) is PdfNumber t && t.IsInteger ? t.LongValue : -1;
            if (type != 0 && type != 2 && type != 3 && type != 4) return null;

            var resolver = doc != null ? (Func<PdfObject, PdfObject>)doc.ResolveObject : null;
            var table = new float[Samples];
            for (int i = 0; i < Samples; i++)
            {
                float k = i / (float)(Samples - 1);
                float[] r = PdfFunctionEvaluator.Evaluate(fn, new[] { k }, resolver);
                float v = r != null && r.Length > 0 && !float.IsNaN(r[0]) ? r[0] : k;
                table[i] = v < clampBelow ? clampBelow : (v > 1f ? 1f : v);
            }
            return table;
        }
    }
}
