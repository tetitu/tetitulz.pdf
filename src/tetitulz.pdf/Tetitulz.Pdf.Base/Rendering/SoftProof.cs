// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Runtime.CompilerServices;
using SkiaSharp;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Soft proofing (<see cref="PdfRenderOptions.ProofProfile"/>): the finished page surface
    /// pushed through an output profile and back — sRGB into the profile's device values by its
    /// relative colorimetric <c>B2A</c> table (<see cref="IccOutputTransform"/>, the plates'
    /// transform, with or without black-point compensation), then the device values to sRGB by the
    /// profile's own <c>A2B</c> table (<see cref="IccProfile.TryEvaluate(float[], IccRenderingIntent, out SKColor)"/>,
    /// the conversion an <c>/ICCBased</c> colour takes into the render). What is left is what the
    /// device can show: its gamut, its paper white, its black.
    /// </summary>
    /// <remarks>
    /// <para><b>One transform, two consumers.</b> Both halves are the renderer's own and each is
    /// checked against an independent colour engine on its own side (RSWOP, within a level); the proof composes them and
    /// adds nothing of its own to the colour arithmetic.</para>
    ///
    /// <para><b>A grid, not a per-pixel evaluation.</b> The composition costs two table walks per
    /// colour, and a page-sized raster has millions of pixels, so it is sampled once at 33 nodes
    /// per channel (<see cref="Grid"/>) and every pixel is read off the grid by trilinear
    /// interpolation — the shape every CMM takes for a device link. Grid nodes are exact (0 and 255
    /// among them, so white stays white and the profile's black is the black); a colour between
    /// nodes may differ from the exact transform by a level or two.</para>
    /// <para><b>Built once per options instance.</b> The grid is cached against the
    /// <see cref="PdfRenderOptions"/> that named the profile (<see cref="For"/>), so a session's
    /// tiles and bands share it. A profile the transform cannot be built from reports
    /// <see cref="RenderDiagnosticKind.ProofProfileUnsupported"/> on every render asked to proof
    /// through it, and that render is left unproofed.</para>
    /// </remarks>
    internal sealed class SoftProof
    {
        /// <summary>Nodes per channel of the sampled transform.</summary>
        internal const int Grid = 33;

        private readonly IccOutputTransform _into;
        private readonly IccProfile _back;
        private readonly int _channels;
        private byte[] _lut;   // Grid³ × 3, red fastest

        private static readonly ConditionalWeakTable<PdfRenderOptions, SoftProof> Cache = new ConditionalWeakTable<PdfRenderOptions, SoftProof>();

        /// <summary>
        /// How many grids have been sampled on this thread: the cache's liveness, for tests — a
        /// grid rebuilt per band gives the same pixels as one built once, so only a count can tell.
        /// </summary>
        [ThreadStatic]
        internal static int GridBuilds;

        private SoftProof(IccOutputTransform into, IccProfile back, int channels)
        {
            _into = into;
            _back = back;
            _channels = channels;
        }

        /// <summary>
        /// The proof <paramref name="profile"/> describes, or <c>null</c> with the
        /// <paramref name="reason"/> when it cannot be built: the bytes are not a profile, it has no
        /// <c>B2A</c> table for the relative intent, or its <c>A2B</c> side cannot be evaluated.
        /// </summary>
        internal static SoftProof TryCreate(byte[] profile, bool blackPointCompensation, out string reason)
        {
            reason = null;
            if (profile == null) { reason = "no profile"; return null; }

            IccOutputTransform into = IccOutputTransform.TryCreate(profile, IccRenderingIntent.MediaRelativeColorimetric, blackPointCompensation, out reason);
            if (into == null) return null;

            IccProfile back = IccProfile.Parse(profile);
            var probe = new float[into.Channels];
            if (!back.TryEvaluate(probe, IccRenderingIntent.MediaRelativeColorimetric, out _))
            {
                reason = "the profile's device-to-PCS side cannot be evaluated" + (back.UnsupportedReason != null ? ": " + back.UnsupportedReason : "");
                return null;
            }
            return new SoftProof(into, back, into.Channels);
        }

        /// <summary>
        /// The proof <paramref name="options"/> asks for — cached on the options — or <c>null</c>
        /// when none is asked for, or when the profile cannot be used, which is reported.
        /// </summary>
        internal static SoftProof For(PdfRenderOptions options)
        {
            if (options?.ProofProfile == null) return null;
            if (Cache.TryGetValue(options, out SoftProof cached)) return cached;

            SoftProof proof = TryCreate(options.ProofProfile, options.ProofBlackPointCompensation, out string reason);
            if (proof == null)
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.ProofProfileUnsupported,
                    "the page was rendered unproofed: " + reason);
                return null;
            }
            // A second thread may have built one meanwhile; either is as good as the other.
            try { Cache.Add(options, proof); }
            catch (ArgumentException) { if (Cache.TryGetValue(options, out cached)) return cached; }
            return proof;
        }

        /// <summary>
        /// Proofs <paramref name="bitmap"/> in place when <paramref name="options"/> asks for it and
        /// <paramref name="plate"/> is not a separation pass; returns the bitmap either way.
        /// </summary>
        internal static SKBitmap Apply(SKBitmap bitmap, PdfRenderOptions options, SeparationPlate plate = null)
        {
            if (bitmap == null || plate != null) return bitmap;
            SoftProof proof = For(options);
            proof?.Apply(bitmap);
            return bitmap;
        }

        /// <summary>Proofs every pixel of <paramref name="bitmap"/>; alpha is kept.</summary>
        internal void Apply(SKBitmap bitmap)
        {
            byte[] lut = _lut ?? (_lut = BuildGrid());
            BitmapPixels.Map(bitmap, straight =>
            {
                Proof(lut, straight.Red, straight.Green, straight.Blue, out byte r, out byte g, out byte b);
                return new SKColor(r, g, b, straight.Alpha);
            });
        }

        /// <summary>The exact composition for one colour — what the grid samples, the bare paper white.</summary>
        internal bool TryProofExactly(SKColor color, out SKColor proofed)
        {
            proofed = color;
            var device = new float[_channels];
            if (!_into.TryConvert(color, device)) return false;
            if (!_back.TryEvaluate(device, IccRenderingIntent.MediaRelativeColorimetric, out proofed)) return false;
            if (NoInk(device)) proofed = SKColors.White;
            return true;
        }

        private byte[] BuildGrid()
        {
            GridBuilds++;
            var lut = new byte[Grid * Grid * Grid * 3];
            for (int bi = 0; bi < Grid; bi++)
                for (int gi = 0; gi < Grid; gi++)
                    for (int ri = 0; ri < Grid; ri++)
                    {
                        var node = new SKColor(Node(ri), Node(gi), Node(bi));
                        // A node the tables cannot place keeps its colour; the bare paper is white, relative
                        // to which everything else is — not the A2B's rounding of it (NoInk).
                        if (!TryProofExactly(node, out SKColor proofed)) proofed = node;
                        int o = ((bi * Grid + gi) * Grid + ri) * 3;
                        lut[o] = proofed.Red; lut[o + 1] = proofed.Green; lut[o + 2] = proofed.Blue;
                    }
            return lut;
        }

        /// <summary>
        /// Whether the device values are the bare paper — no ink on any channel, which the output
        /// transform's white fix-up answers for sRGB white. Taken back through the <c>A2B</c> table
        /// that is the media white, which under the relative intent is the PCS white and so sRGB
        /// white — but by way of Lab, XYZ and a gamma, which rounds a level off (255, 255, 254 on a
        /// synthetic profile and on RSWOP alike). A proof's paper is white by definition of the
        /// relative intent, so the node is written as white outright.
        /// </summary>
        private static bool NoInk(float[] device)
        {
            foreach (float v in device) if (v > 0) return false;
            return true;
        }

        /// <summary>The channel value at grid node <paramref name="i"/>: 0 at the first, 255 at the last.</summary>
        internal static byte Node(int i) => PdfColor.Channel(i / (float)(Grid - 1));

        /// <summary>One colour read off the grid by trilinear interpolation.</summary>
        internal static void Proof(byte[] lut, byte red, byte green, byte blue, out byte r, out byte g, out byte b)
        {
            // Position in grid units: 0..Grid-1, the last node at 255.
            float fr = red * (Grid - 1) / 255f, fg = green * (Grid - 1) / 255f, fb = blue * (Grid - 1) / 255f;
            int r0 = Math.Min(SaturatingCast.ToInt(fr), Grid - 2), g0 = Math.Min(SaturatingCast.ToInt(fg), Grid - 2), b0 = Math.Min(SaturatingCast.ToInt(fb), Grid - 2);
            float tr = fr - r0, tg = fg - g0, tb = fb - b0;

            float accR = 0, accG = 0, accB = 0;
            for (int db = 0; db < 2; db++)
                for (int dg = 0; dg < 2; dg++)
                    for (int dr = 0; dr < 2; dr++)
                    {
                        float w = (dr == 0 ? 1 - tr : tr) * (dg == 0 ? 1 - tg : tg) * (db == 0 ? 1 - tb : tb);
                        if (w == 0) continue;
                        int o = (((b0 + db) * Grid + (g0 + dg)) * Grid + (r0 + dr)) * 3;
                        accR += w * lut[o]; accG += w * lut[o + 1]; accB += w * lut[o + 2];
                    }
            r = PdfColor.ClampChannel(accR);
            g = PdfColor.ClampChannel(accG);
            b = PdfColor.ClampChannel(accB);
        }
    }
}
