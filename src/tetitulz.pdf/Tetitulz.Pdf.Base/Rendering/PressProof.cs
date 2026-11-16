// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Runtime.CompilerServices;
using SkiaSharp;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// The overprint preview proofed through a press: with a CMYK
    /// <see cref="PdfRenderOptions.ProofProfile"/>, the preview's plates are that press's — every colour without
    /// ink of its own separated through the profile's <c>B2A</c> table, as <see cref="PdfSeparationOptions.OutputIccProfile"/>
    /// separates it — and the process plates come back through the profile's own <c>A2B</c> table, as the press
    /// prints them, instead of the composite's naive conversion and a proof of that. What an overprint
    /// preview shows with a simulation profile.
    /// </summary>
    /// <remarks>
    /// <para><b>Exact while a page's inks are few.</b> A render converts each distinct ink combination through the
    /// <c>A2B</c> table itself, remembered for the rest of the render (<see cref="Session"/>), up to
    /// <see cref="MaxExactColours"/> of them — every vector page, and most pages. Past that (a photograph's
    /// plates) the rest are read off a grid sampled once at 17 nodes per ink by quadrilinear interpolation: nodes
    /// are exact, and between them a colour can be some levels off where the table's output clips to sRGB — eleven,
    /// the worst of 2 000 random colours through RSWOP, at a saturated red. No ink is the paper — white.</para>
    /// <para>A proof profile without a CMYK <c>B2A</c> table (an RGB printer's) has no press plates: the preview
    /// is recombined as before and the proof applied to it, which reports what it cannot use.</para>
    /// </remarks>
    internal sealed class PressProof
    {
        /// <summary>Nodes per ink of the sampled <c>A2B</c> table.</summary>
        internal const int Grid = 17;

        private readonly IccProfile _back;
        private byte[] _lut;   // Grid⁴ × 3, cyan fastest

        private static readonly ConditionalWeakTable<PdfRenderOptions, PressProof> Cache = new ConditionalWeakTable<PdfRenderOptions, PressProof>();

        /// <summary>How many grids have been sampled on this thread: the cache's liveness, for tests.</summary>
        [ThreadStatic]
        internal static int GridBuilds;

        private PressProof(SeparationOutput output, IccProfile back)
        {
            Output = output;
            _back = back;
        }

        /// <summary>How the preview's plates are separated: the press's <c>B2A</c> tables.</summary>
        internal SeparationOutput Output { get; }

        /// <summary>
        /// The press <paramref name="options"/>' proof profile is, cached on the options, or null: no proof
        /// asked for, or a profile that is not a CMYK press both ways.
        /// </summary>
        internal static PressProof For(PdfRenderOptions options)
        {
            if (options?.ProofProfile == null) return null;
            if (Cache.TryGetValue(options, out PressProof cached)) return cached;

            SeparationOutput output = SeparationOutput.TryCreate(options.ProofProfile, options.ProofBlackPointCompensation, out _);
            if (output == null) return null;
            IccProfile back = IccProfile.Parse(options.ProofProfile);
            if (back.ComponentCount != 4 || !back.TryEvaluate(new float[4], IccRenderingIntent.MediaRelativeColorimetric, out _)) return null;

            var press = new PressProof(output, back);
            try { Cache.Add(options, press); }
            catch (ArgumentException) { if (Cache.TryGetValue(options, out cached)) return cached; }
            return press;
        }

        /// <summary>How many distinct ink combinations one render converts exactly before it reads the grid.</summary>
        internal const int MaxExactColours = 1 << 16;

        /// <summary>
        /// One render's conversion: each combination through the table once and remembered, the grid past
        /// <paramref name="maxExact"/> of them. Its own memory, so renders on other threads share nothing.
        /// </summary>
        internal Func<byte, byte, byte, byte, SKColor> Session(int maxExact = MaxExactColours)
        {
            var seen = new System.Collections.Generic.Dictionary<uint, SKColor>();
            return (c, m, y, k) =>
            {
                uint key = c | ((uint)m << 8) | ((uint)y << 16) | ((uint)k << 24);
                if (seen.TryGetValue(key, out SKColor colour)) return colour;
                if (seen.Count >= maxExact) return ToSrgb(c, m, y, k);
                colour = Exactly(c, m, y, k);
                seen.Add(key, colour);
                return colour;
            };
        }

        /// <summary>The press's colour of the inks (0 … 255 each), read off the grid.</summary>
        internal SKColor ToSrgb(byte c, byte m, byte y, byte k)
        {
            byte[] lut = _lut ?? (_lut = BuildGrid());
            const int n = Grid;
            float fc = c * (n - 1) / 255f, fm = m * (n - 1) / 255f, fy = y * (n - 1) / 255f, fk = k * (n - 1) / 255f;
            int c0 = Math.Min(SaturatingCast.ToInt(fc), n - 2), m0 = Math.Min(SaturatingCast.ToInt(fm), n - 2), y0 = Math.Min(SaturatingCast.ToInt(fy), n - 2), k0 = Math.Min(SaturatingCast.ToInt(fk), n - 2);
            float tc = fc - c0, tm = fm - m0, ty = fy - y0, tk = fk - k0;

            float r = 0, g = 0, b = 0;
            for (int dk = 0; dk < 2; dk++)
                for (int dy = 0; dy < 2; dy++)
                    for (int dm = 0; dm < 2; dm++)
                        for (int dc = 0; dc < 2; dc++)
                        {
                            float w = (dc == 0 ? 1 - tc : tc) * (dm == 0 ? 1 - tm : tm) * (dy == 0 ? 1 - ty : ty) * (dk == 0 ? 1 - tk : tk);
                            if (w == 0) continue;
                            int o = ((((k0 + dk) * n + (y0 + dy)) * n + (m0 + dm)) * n + (c0 + dc)) * 3;
                            r += w * lut[o]; g += w * lut[o + 1]; b += w * lut[o + 2];
                        }
            return new SKColor(PdfColor.ClampChannel(r), PdfColor.ClampChannel(g), PdfColor.ClampChannel(b));
        }

        /// <summary>The press's colour of the inks through its <c>A2B</c> table exactly — what the grid samples.</summary>
        internal SKColor Exactly(byte c, byte m, byte y, byte k)
        {
            if (c == 0 && m == 0 && y == 0 && k == 0) return SKColors.White;
            var inks = new[] { c / 255f, m / 255f, y / 255f, k / 255f };
            return _back.TryEvaluate(inks, IccRenderingIntent.MediaRelativeColorimetric, out SKColor color)
                ? color
                : PdfColor.CmykToRgb(inks[0], inks[1], inks[2], inks[3]);
        }

        private byte[] BuildGrid()
        {
            GridBuilds++;
            const int n = Grid;
            var lut = new byte[n * n * n * n * 3];
            for (int ki = 0; ki < n; ki++)
                for (int yi = 0; yi < n; yi++)
                    for (int mi = 0; mi < n; mi++)
                        for (int ci = 0; ci < n; ci++)
                        {
                            SKColor colour = Exactly(Node(ci), Node(mi), Node(yi), Node(ki));
                            int o = (((ki * n + yi) * n + mi) * n + ci) * 3;
                            lut[o] = colour.Red; lut[o + 1] = colour.Green; lut[o + 2] = colour.Blue;
                        }
            return lut;
        }

        /// <summary>The ink at grid node <paramref name="i"/>: none at the first, full at the last.</summary>
        internal static byte Node(int i) => PdfColor.Channel(i / (float)(Grid - 1));
    }
}
